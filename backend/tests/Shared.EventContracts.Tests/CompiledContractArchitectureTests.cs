using ArchUnitNET.Domain;
using ArchUnitNET.Fluent.Syntax.Elements.Types.Classes;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Shared.EventContracts;
using Shared.Events;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Shared.EventContracts.Tests;

public sealed class CompiledContractArchitectureTests
{
    private static readonly Architecture ContractArchitecture = new ArchLoader()
        .LoadAssemblies(typeof(AuditLogWriteRequestedTenantEto).Assembly)
        .Build();

    private static readonly ClassesShouldConjunctionWithDescription ClosedMessageRule = Classes()
        .That().HaveAnyAttributes(typeof(DistributedContractAttribute))
        .Should().BeSealed()
        .Because("distributed message schemas must not acquire unregistered derived payloads");

    [Fact]
    public void Distributed_contract_selection_should_include_the_known_tenant_message()
    {
        var contracts = Classes().That().HaveAnyAttributes(typeof(DistributedContractAttribute))
            .GetObjects(ContractArchitecture);

        Assert.Contains(contracts, contract =>
            string.Equals(contract.FullName, typeof(AuditLogWriteRequestedTenantEto).FullName, StringComparison.Ordinal));
    }

    [Fact]
    public void Distributed_contracts_should_have_closed_message_shapes()
    {
        ClosedMessageRule.Check(ContractArchitecture);
    }

    [Fact]
    public void Closed_message_rule_should_detect_an_inheritable_contract()
    {
        var fixtureArchitecture = new ArchLoader()
            .LoadAssemblies(typeof(InheritableContractFixture).Assembly)
            .Build();

        Assert.False(ClosedMessageRule.HasNoViolations(fixtureArchitecture));
    }

    // Deliberately violates the rule; this test-only fixture is never registered
    // in the production distributed contract catalog.
    [DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
    public class InheritableContractFixture;
}
