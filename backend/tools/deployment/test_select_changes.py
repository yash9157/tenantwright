import json
import io
from http.client import IncompleteRead
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from select_changes import (
    changed_services_since_checkpoints,
    delivery_plan,
    github_json,
    published_image_checkpoints,
    reuse_run_groups,
    select_changes,
    successful_base,
)


class SelectionTests(unittest.TestCase):
    services = [{'service': 'identity'}, {'service': 'auth'}]
    prefixes = {'identity': ['services/Identity/'], 'auth': ['app/Auth/']}

    def select(self, files):
        with patch('select_changes.subprocess.check_output', return_value=''):
            return select_changes(files, self.services, self.prefixes)

    def test_ci_only_does_not_build_or_deploy(self):
        self.assertEqual(self.select(['.github/workflows/tenantwright-ci.yml', 'tools/deployment/deploy_dev.py']), ([], []))

    def test_chart_only_retains_image(self):
        self.assertEqual(self.select(['charts/tenantwright-identity/values.yaml']), ([], ['identity']))

    def test_source_and_chart_have_one_image_promotion(self):
        self.assertEqual(self.select(['services/Identity/User.cs', 'charts/tenantwright-identity/values.yaml']), (['identity'], []))

    def test_shared_chart_selects_all_without_rebuilding(self):
        self.assertEqual(self.select(['charts/tenantwright-common/templates/_config.tpl']), ([], ['identity', 'auth']))

    def test_dev_component_values_select_one_chart_without_image_builds(self):
        self.assertEqual(self.select(['charts/environments/dev/backend/identity.values.yaml']), ([], ['identity']))

    def test_dev_common_values_select_all_charts_without_image_builds(self):
        for file in ['backend/common.values.yaml', 'backend/messaging.values.yaml', 'images.lock.yaml']:
            self.assertEqual(self.select(['charts/environments/dev/' + file]), ([], ['identity', 'auth']))

    def test_other_environment_private_and_platform_inputs_do_not_deploy_dev_apps(self):
        for file in ['charts/environments/production/backend/auth.values.yaml.example',
                     'charts/environments/dev/private/backend/auth.values.local.yaml',
                     'charts/environments/dev/platform/rabbitmq.values.yaml']:
            self.assertEqual(self.select([file]), ([], []))

    def test_transitive_project_consumers(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            projects = {
                'shared/Contracts/Contracts.csproj': '<Project/>',
                'services/Identity/Identity.csproj': '<Project><ItemGroup><ProjectReference Include="../../shared/Contracts/Contracts.csproj"/></ItemGroup></Project>',
                'app/Auth/Auth.csproj': '<Project/>',
            }
            for name, content in projects.items():
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content)
            with patch('select_changes.subprocess.check_output', return_value='\n'.join(projects)):
                self.assertEqual(select_changes(['shared/Contracts/Dto.cs'], self.services, self.prefixes, root), (['identity'], []))

    def test_successful_baseline_ignores_current_run(self):
        runs = {'workflow_runs': [{'id': 12, 'head_sha': 'current'}, {'id': 9, 'head_sha': 'last-success'}]}
        with patch.dict('select_changes.os.environ', {'GH_TOKEN': 'test-token'}, clear=False), \
             patch('select_changes.urlopen', return_value=io.BytesIO(json.dumps(runs).encode())) as api:
            self.assertEqual(successful_base('owner/repo', 'dev', '12', 'tenantwright-ci.yml'), 'last-success')
            self.assertIn('status=success', api.call_args.args[0].full_url)

    def test_truncated_successful_baseline_response_falls_back_safely(self):
        with patch.dict('select_changes.os.environ', {'GH_TOKEN': 'test-token'}, clear=False), \
             patch('select_changes.github_json', side_effect=IncompleteRead(b'{}', 1)):
            self.assertIsNone(successful_base('owner/repo', 'dev', '12', 'tenantwright-ci.yml'))

    def test_github_json_retries_a_truncated_response(self):
        response = {'workflow_runs': []}
        with patch('select_changes.urlopen', side_effect=[IncompleteRead(b'{}', 1),
                                                          io.BytesIO(json.dumps(response).encode())]), \
             patch('select_changes.time.sleep') as delay:
            self.assertEqual(github_json('owner/repo', 'actions/runs', 'test-token'), response)
            delay.assert_called_once_with(1)

    def test_ci_only_push_retries_pending_deploy_without_rebuilding_published_images(self):
        self.assertEqual(delivery_plan(['identity', 'auth'], ['identity', 'auth'], [],
                                       {'identity', 'auth'}, []),
                         ([], ['identity', 'auth'], ['identity', 'auth'], []))

    def test_mixed_new_and_reused_images_keep_one_deployment_selection(self):
        self.assertEqual(delivery_plan(['identity', 'auth'], ['identity', 'auth'], ['auth'],
                                       {'identity', 'auth'}, []),
                         (['auth'], ['identity', 'auth'], ['identity'], []))

    def test_missing_prior_artifact_rebuilds_only_uncovered_pending_image(self):
        self.assertEqual(delivery_plan(['identity', 'auth'], ['identity', 'auth'], [],
                                       {'auth'}, []),
                         (['identity'], ['identity', 'auth'], ['auth'], []))

    def test_chart_only_release_retains_deployed_digest(self):
        self.assertEqual(delivery_plan(['identity', 'auth'], [], [], set(), ['auth']),
                         ([], ['auth'], [], ['auth']))

    def test_ci_only_without_pending_deployment_is_noop(self):
        self.assertEqual(delivery_plan(['identity', 'auth'], [], [], set(), []),
                         ([], [], [], []))

    def test_failed_deployment_can_supply_successful_image_checkpoint(self):
        digest = 'sha256:' + 'a' * 64
        run = {'id': 9, 'head_sha': 'published-sha', 'status': 'completed', 'conclusion': 'failure'}
        jobs = {'total_count': 2, 'jobs': [
            {'name': 'Docker Build / Publish (identity)', 'conclusion': 'success'},
            {'name': 'Docker Build / Publish (auth)', 'conclusion': 'success'},
        ]}
        artifacts = {'total_count': 2, 'artifacts': [
            {'name': f'tenantwright-image-digest-{service}', 'expired': False, 'digest': digest,
             'workflow_run': {'id': 9, 'head_sha': 'published-sha'}}
            for service in ('identity', 'auth')
        ]}
        def api(_repository, endpoint, _token):
            if '/jobs?' in endpoint:
                return jobs
            if '/artifacts?' in endpoint:
                return artifacts
            return {'workflow_runs': [run]}
        with patch.dict('select_changes.os.environ', {'GH_TOKEN': 'test-token'}), \
             patch('select_changes.github_json', side_effect=api), \
             patch('select_changes.subprocess.run') as ancestry:
            ancestry.return_value.returncode = 0
            self.assertEqual(published_image_checkpoints('owner/repo', 'dev', 10, 'tenantwright-ci.yml',
                                                         'current-sha', {'identity', 'auth'}),
                             {
                                 'identity': {'head_sha': 'published-sha', 'run_id': '9'},
                                 'auth': {'head_sha': 'published-sha', 'run_id': '9'},
                             })

    def test_failed_runs_accumulate_the_newest_checkpoint_per_service(self):
        runs = {'total_count': 2, 'workflow_runs': [
            {'id': 9, 'head_sha': 'identity-sha', 'status': 'completed'},
            {'id': 8, 'head_sha': 'auth-sha', 'status': 'completed'},
        ]}
        jobs = {
            '9': {'total_count': 1, 'jobs': [
                {'name': 'Docker Build / Publish (identity)', 'conclusion': 'success'},
            ]},
            '8': {'total_count': 2, 'jobs': [
                {'name': 'Docker Build / Publish (auth)', 'conclusion': 'success'},
                {'name': 'Docker Build / Publish (identity)', 'conclusion': 'success'},
            ]},
        }
        digest = 'sha256:' + 'a' * 64
        artifacts = {
            '9': {'total_count': 1, 'artifacts': [
                {'name': 'tenantwright-image-digest-identity', 'expired': False, 'digest': digest,
                 'workflow_run': {'id': 9, 'head_sha': 'identity-sha'}},
            ]},
            '8': {'total_count': 2, 'artifacts': [
                {'name': f'tenantwright-image-digest-{service}', 'expired': False, 'digest': digest,
                 'workflow_run': {'id': 8, 'head_sha': 'auth-sha'}}
                for service in ('auth', 'identity')
            ]},
        }

        def api(_repository, endpoint, _token):
            if '/jobs?' in endpoint:
                return jobs[endpoint.split('/')[2]]
            if '/artifacts?' in endpoint:
                return artifacts[endpoint.split('/')[2]]
            return runs

        with patch.dict('select_changes.os.environ', {'GH_TOKEN': 'test-token'}), \
             patch('select_changes.github_json', side_effect=api), \
             patch('select_changes.subprocess.run') as ancestry:
            ancestry.return_value.returncode = 0
            self.assertEqual(published_image_checkpoints('owner/repo', 'dev', 10, 'tenantwright-ci.yml',
                                                         'current-sha', {'identity', 'auth'}),
                             {
                                 'identity': {'head_sha': 'identity-sha', 'run_id': '9'},
                                 'auth': {'head_sha': 'auth-sha', 'run_id': '8'},
                             })

    def test_failed_job_expired_or_foreign_artifact_is_not_reused(self):
        digest = 'sha256:' + 'a' * 64
        responses = [
            {'workflow_runs': [{'id': 9, 'head_sha': 'published-sha', 'status': 'completed'}]},
            {'total_count': 2, 'jobs': [
                {'name': 'Docker Build / Publish (identity)', 'conclusion': 'success'},
                {'name': 'Docker Build / Publish (auth)', 'conclusion': 'failure'},
            ]},
            {'total_count': 3, 'artifacts': [
                {'name': 'tenantwright-image-digest-identity', 'expired': True, 'digest': digest,
                 'workflow_run': {'id': 9, 'head_sha': 'published-sha'}},
                {'name': 'tenantwright-image-digest-identity', 'expired': False, 'digest': digest,
                 'workflow_run': {'id': 8, 'head_sha': 'foreign-sha'}},
                {'name': 'tenantwright-image-digest-auth', 'expired': False, 'digest': digest,
                 'workflow_run': {'id': 9, 'head_sha': 'published-sha'}},
            ]},
        ]
        with patch.dict('select_changes.os.environ', {'GH_TOKEN': 'test-token'}), \
             patch('select_changes.github_json', side_effect=responses), \
             patch('select_changes.subprocess.run') as ancestry:
            ancestry.return_value.returncode = 0
            self.assertEqual(published_image_checkpoints('owner/repo', 'dev', 10, 'tenantwright-ci.yml',
                                                         'current-sha', {'identity', 'auth'}), {})

    def test_unavailable_image_metadata_cannot_certify_prior_digest(self):
        for failure in (OSError('offline'), IncompleteRead(b'{}', 1)):
            with self.subTest(failure=type(failure).__name__), \
                 patch.dict('select_changes.os.environ', {'GH_TOKEN': 'test-token'}), \
                 patch('select_changes.github_json', side_effect=failure):
                self.assertEqual(published_image_checkpoints('owner/repo', 'dev', 10, 'tenantwright-ci.yml',
                                                             'current-sha', {'identity', 'auth'}), {})

    def test_metadata_failure_preserves_only_already_certified_checkpoints(self):
        digest = 'sha256:' + 'a' * 64
        responses = [
            {'total_count': 2, 'workflow_runs': [
                {'id': 9, 'head_sha': 'identity-sha', 'status': 'completed'},
                {'id': 8, 'head_sha': 'auth-sha', 'status': 'completed'},
            ]},
            {'total_count': 1, 'jobs': [
                {'name': 'Docker Build / Publish (identity)', 'conclusion': 'success'},
            ]},
            {'total_count': 1, 'artifacts': [
                {'name': 'tenantwright-image-digest-identity', 'expired': False, 'digest': digest,
                 'workflow_run': {'id': 9, 'head_sha': 'identity-sha'}},
            ]},
            OSError('offline'),
        ]
        with patch.dict('select_changes.os.environ', {'GH_TOKEN': 'test-token'}), \
             patch('select_changes.github_json', side_effect=responses), \
             patch('select_changes.subprocess.run') as ancestry:
            ancestry.return_value.returncode = 0
            self.assertEqual(published_image_checkpoints('owner/repo', 'dev', 10, 'tenantwright-ci.yml',
                                                         'current-sha', {'identity', 'auth'}),
                             {'identity': {'head_sha': 'identity-sha', 'run_id': '9'}})

    def test_each_service_is_invalidated_only_by_changes_after_its_checkpoint(self):
        checkpoints = {
            'identity': {'head_sha': 'identity-sha', 'run_id': '9'},
            'auth': {'head_sha': 'auth-sha', 'run_id': '8'},
        }

        def git_output(args, **_kwargs):
            if args[1] == 'diff':
                return ('services/Identity/User.cs\n' if args[3] == 'identity-sha'
                        else '.github/workflows/tenantwright-ci.yml\n')
            if args[1] == 'ls-files':
                return ''
            raise AssertionError(args)

        with patch('select_changes.subprocess.check_output', side_effect=git_output):
            self.assertEqual(changed_services_since_checkpoints(
                checkpoints, 'current-sha', self.services, self.prefixes), {'identity'})

    def test_services_sharing_a_checkpoint_are_compared_once(self):
        checkpoints = {
            'identity': {'head_sha': 'shared-sha', 'run_id': '9'},
            'auth': {'head_sha': 'shared-sha', 'run_id': '9'},
        }
        with patch('select_changes.subprocess.check_output', side_effect=[
                 'services/Identity/User.cs\n', '']) as git_output:
            self.assertEqual(changed_services_since_checkpoints(
                checkpoints, 'current-sha', self.services, self.prefixes), {'identity'})
            diff_calls = [call for call in git_output.call_args_list if call.args[0][1] == 'diff']
            self.assertEqual(len(diff_calls), 1)

    def test_reused_services_are_grouped_by_their_source_run(self):
        checkpoints = {
            'identity': {'head_sha': 'identity-sha', 'run_id': '9'},
            'auth': {'head_sha': 'auth-sha', 'run_id': '8'},
        }
        self.assertEqual(reuse_run_groups(checkpoints, ['identity', 'auth'], ['identity', 'auth']),
                         [
                             {'run_id': '9', 'services': 'identity'},
                             {'run_id': '8', 'services': 'auth'},
                         ])


if __name__ == '__main__':
    unittest.main()
