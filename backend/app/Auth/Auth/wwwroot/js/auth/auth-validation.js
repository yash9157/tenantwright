(function () {
    var INPUT_INVALID_CLASSES = [
        "border-danger/60",
        "bg-danger-soft/40",
        "placeholder:text-danger/60",
        "focus:border-danger",
        "focus:ring-danger/20"
    ];
    var INPUT_VALID_CLASSES = [
        "border-success/40",
        "bg-success-soft/35",
        "focus:border-success",
        "focus:ring-success/20"
    ];

    var STRATEGIES = {
        tenant: {
            mode: "blur-first",
            normalizeOnBlur: "trim",
            validate: function (value) {
                var normalized = (value || "").trim();
                if (!normalized) {
                    return "Organization ID is required.";
                }

                if (normalized.length < 2 || normalized.length > 64) {
                    return "Enter a valid Organization ID.";
                }

                if (!/^[a-zA-Z0-9._-]+$/.test(normalized)) {
                    return "Enter a valid Organization ID.";
                }

                return "";
            }
        },
        "login-id": {
            mode: "blur-first",
            normalizeOnBlur: "trim",
            validate: function (value) {
                var normalized = (value || "").trim();
                if (!normalized) {
                    return "Email or username is required.";
                }

                if (normalized.length < 3 || normalized.length > 256) {
                    return "Enter a valid email or username.";
                }

                if (normalized.indexOf("@") >= 0) {
                    return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(normalized)
                        ? ""
                        : "Enter a valid email or username.";
                }

                return /^[A-Za-z0-9._-]+$/.test(normalized)
                    ? ""
                    : "Enter a valid email or username.";
            }
        },
        username: {
            mode: "blur-first",
            normalizeOnBlur: "trim",
            validate: function (value) {
                var normalized = (value || "").trim();
                if (!normalized) {
                    return "Username is required.";
                }

                if (normalized.length < 3 || normalized.length > 256) {
                    return "Enter a valid username.";
                }

                return /^[A-Za-z0-9._-]+$/.test(normalized)
                    ? ""
                    : "Enter a valid username.";
            }
        },
        email: {
            mode: "blur-first",
            normalizeOnBlur: "trim",
            validate: function (value) {
                var normalized = (value || "").trim();
                if (!normalized) {
                    return "Email is required.";
                }

                if (normalized.length > 256) {
                    return "Enter a valid work email.";
                }

                return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(normalized)
                    ? ""
                    : "Enter a valid work email.";
            }
        },
        phone: {
            mode: "blur-first",
            normalizeOnBlur: "trim",
            validate: function (value) {
                var normalized = (value || "").trim();
                if (!normalized) {
                    return "";
                }

                if (normalized.length > 32) {
                    return "Enter a valid phone number.";
                }

                return /^[0-9+()\-\s]+$/.test(normalized)
                    ? ""
                    : "Enter a valid phone number.";
            }
        },
        "current-password": {
            mode: "input-live",
            validate: function (value, field) {
                if (!value) {
                    return field.requiredMessage || "Password is required.";
                }

                if (value.length > 256) {
                    return "Password is too long.";
                }

                return "";
            }
        },
        "new-password": {
            mode: "input-live",
            validate: function (value, field, form) {
                if (!value) {
                    return field.requiredMessage || "Password is required.";
                }

                if (value.length > 256) {
                    return "Password is too long.";
                }

                var policy = getPasswordPolicy(form);
                if (value.length < policy.requiredLength) {
                    return "Password must contain at least " + policy.requiredLength + " characters.";
                }

                if (policy.requireLowercase && !/[a-z]/.test(value)) {
                    return "Password must include a lowercase letter.";
                }

                if (policy.requireUppercase && !/[A-Z]/.test(value)) {
                    return "Password must include an uppercase letter.";
                }

                if (policy.requireDigit && !/\d/.test(value)) {
                    return "Password must include a number.";
                }

                if (policy.requireNonAlphanumeric && !/[^A-Za-z0-9]/.test(value)) {
                    return "Password must include a symbol.";
                }

                return "";
            }
        },
        "confirm-password": {
            mode: "input-live",
            validate: function (value, field) {
                if (!value) {
                    return field.requiredMessage || "Confirm your password.";
                }

                var matchSelector = field.input.getAttribute("data-auth-password-match");
                if (!matchSelector) {
                    return "";
                }

                var matchInput = field.form.querySelector(matchSelector);
                if (!matchInput) {
                    return "";
                }

                return value === matchInput.value
                    ? ""
                    : "Passwords do not match.";
            }
        },
        otp: {
            mode: "input-live",
            validate: function (value) {
                if (!value) {
                    return "Enter the complete 6-digit code.";
                }

                return /^\d{6}$/.test(value)
                    ? ""
                    : "Enter the complete 6-digit code.";
            }
        },
        "recovery-code": {
            mode: "blur-first",
            validate: function (value) {
                var normalized = (value || "").trim();
                if (!normalized) {
                    return "Recovery code is required.";
                }

                if (normalized.length < 6 || normalized.length > 128) {
                    return "Enter a valid recovery code.";
                }

                return /^[A-Za-z0-9-]+$/.test(normalized)
                    ? ""
                    : "Enter a valid recovery code.";
            }
        }
    };

    function getPasswordPolicy(form) {
        var rawPolicy = form.getAttribute("data-auth-password-policy");
        if (!rawPolicy) {
            return {
                requiredLength: 8,
                requireDigit: true,
                requireLowercase: true,
                requireUppercase: true,
                requireNonAlphanumeric: true
            };
        }

        try {
            var parsed = JSON.parse(rawPolicy);
            return {
                requiredLength: Number(parsed.requiredLength || parsed.RequiredLength || 8),
                requireDigit: Boolean(parsed.requireDigit ?? parsed.RequireDigit),
                requireLowercase: Boolean(parsed.requireLowercase ?? parsed.RequireLowercase),
                requireUppercase: Boolean(parsed.requireUppercase ?? parsed.RequireUppercase),
                requireNonAlphanumeric: Boolean(parsed.requireNonAlphanumeric ?? parsed.RequireNonAlphanumeric)
            };
        } catch (_error) {
            return {
                requiredLength: 8,
                requireDigit: true,
                requireLowercase: true,
                requireUppercase: true,
                requireNonAlphanumeric: true
            };
        }
    }

    function setButtonLoading(button) {
        if (!button || button.dataset.authLoadingApplied === "true") {
            return;
        }

        var loadingText = button.getAttribute("data-auth-loading-text") || "Processing...";
        button.dataset.originalText = button.innerHTML;
        button.dataset.authLoadingApplied = "true";
        button.innerHTML =
            "<span class='inline-block size-4 animate-spin rounded-full border-2 border-white/35 border-t-white' aria-hidden='true'></span><span>" +
            loadingText +
            "</span>";
    }

    function getSummaryElement(form) {
        return form.querySelector("[data-auth-summary='true']") ||
            form.querySelector("[data-valmsg-summary='true']") ||
            form.querySelector("[asp-validation-summary='ModelOnly']");
    }

    function ensureSummaryList(summary) {
        var list = summary.querySelector("ul");
        if (!list) {
            list = document.createElement("ul");
            summary.replaceChildren(list);
        }

        return list;
    }

    function setSummaryMessages(form, messages) {
        var summary = getSummaryElement(form);
        if (!summary) {
            return;
        }

        if (!messages || messages.length === 0) {
            summary.classList.remove("validation-summary-errors");
            summary.classList.add("validation-summary-valid");
            summary.setAttribute("hidden", "hidden");
            return;
        }

        var list = ensureSummaryList(summary);
        list.replaceChildren();
        messages.forEach(function (message) {
            var item = document.createElement("li");
            item.textContent = message;
            list.appendChild(item);
        });

        summary.classList.add("validation-summary-errors");
        summary.classList.remove("validation-summary-valid");
        summary.removeAttribute("hidden");
    }

    function toggleClasses(element, classes, shouldAdd) {
        if (!element) {
            return;
        }

        classes.forEach(function (className) {
            element.classList.toggle(className, shouldAdd);
        });
    }

    function getFieldState(input) {
        var strategyName = input.getAttribute("data-auth-validate");
        var strategy = STRATEGIES[strategyName];
        if (!strategy) {
            return null;
        }

        var form = input.form;
        var name = input.getAttribute("name") || "";
        var errorElement = name
            ? form.querySelector("[data-valmsg-for='" + CSS.escape(name) + "']")
            : null;
        var container = input.closest("[data-auth-field='true']");

        if (!container && strategyName === "otp") {
            container = input.closest("[data-auth-field='true']");
        }

        if (errorElement && !errorElement.id) {
            errorElement.id = name.replaceAll(".", "_") + "_error";
        }

        return {
            form: form,
            input: input,
            name: name,
            strategyName: strategyName,
            strategy: strategy,
            errorElement: errorElement,
            container: container,
            touched: false,
            blurred: false,
            submitted: false,
            requiredMessage: input.getAttribute("data-auth-required-message") || "",
            focusTargetSelector: input.getAttribute("data-auth-focus-target") || ""
        };
    }

    function setErrorState(field, message) {
        var isValid = !message;
        if (field.errorElement) {
            field.errorElement.textContent = message || "";
            field.errorElement.classList.toggle("field-validation-error", !isValid);
            field.errorElement.classList.toggle("field-validation-valid", isValid);
        }

        if (field.input.type !== "hidden") {
            field.input.setAttribute("aria-invalid", isValid ? "false" : "true");
            toggleClasses(field.input, INPUT_INVALID_CLASSES, !isValid);
            toggleClasses(
                field.input,
                INPUT_VALID_CLASSES,
                isValid && (field.touched || field.submitted) && getFieldValue(field)
            );
        }

        if (field.errorElement && message && field.input.type !== "hidden") {
            field.input.setAttribute("aria-describedby", field.errorElement.id);
        } else if (field.input.type !== "hidden") {
            field.input.removeAttribute("aria-describedby");
        }

        if (field.container) {
            if (message) {
                field.container.setAttribute("data-auth-invalid", "true");
                field.container.removeAttribute("data-auth-valid");
            } else if ((field.touched || field.submitted) && getFieldValue(field)) {
                field.container.setAttribute("data-auth-valid", "true");
                field.container.removeAttribute("data-auth-invalid");
            } else {
                field.container.removeAttribute("data-auth-valid");
                field.container.removeAttribute("data-auth-invalid");
            }
        }

        if (field.strategyName === "otp") {
            var otpContainer = field.form.querySelector("[data-auth-otp-target='" + field.input.id + "']");
            if (otpContainer) {
                otpContainer.setAttribute("data-auth-invalid", message ? "true" : "false");
                otpContainer.querySelectorAll("input[data-auth-otp-slot='true']").forEach(function (slot) {
                    toggleClasses(slot, INPUT_INVALID_CLASSES, !isValid);
                    toggleClasses(
                        slot,
                        INPUT_VALID_CLASSES,
                        isValid && (field.touched || field.submitted) && getFieldValue(field)
                    );
                });
            }
        }
    }

    function getFieldValue(field) {
        return field.input.value || "";
    }

    // Compute validity of ALL validated fields without rendering any error UI.
    // Used to gate the submit button (disable until every required field is valid).
    function isFormValid(fieldStates) {
        return fieldStates.every(function (field) {
            return !field.strategy.validate(getFieldValue(field), field, field.form);
        });
    }

    function refreshSubmitState(form, fieldStates) {
        if (form.dataset.authSubmitting === "true") {
            return;
        }

        var valid = isFormValid(fieldStates);
        form.querySelectorAll("button[type='submit']").forEach(function (button) {
            if (button.hasAttribute("data-auth-allow-submit")) {
                return;
            }
            button.disabled = !valid;
            button.setAttribute("aria-disabled", valid ? "false" : "true");
        });
    }

    function normalizeFieldValue(field, reason) {
        if (field.strategy.normalizeOnBlur === "trim" && (reason === "blur" || reason === "submit")) {
            field.input.value = (field.input.value || "").trim();
        }
    }

    function shouldValidateOnInput(field, formState) {
        if (field.strategy.mode === "input-live") {
            return true;
        }

        return field.touched || field.blurred || formState.submitted;
    }

    function validateField(field, formState, reason) {
        normalizeFieldValue(field, reason);
        var message = field.strategy.validate(getFieldValue(field), field, field.form);
        field.submitted = formState.submitted;
        setErrorState(field, message);
        return message;
    }

    function syncSummaryState(form, fieldStates, formState) {
        if (!formState.submitted) {
            return;
        }

        var hasInvalidField = fieldStates.some(function (field) {
            return field.errorElement && field.errorElement.textContent.trim().length > 0;
        });

        if (hasInvalidField) {
            setSummaryMessages(form, ["Please correct the highlighted fields and try again."]);
            return;
        }

        setSummaryMessages(form, []);
    }

    function focusField(field) {
        if (field.focusTargetSelector) {
            var focusTarget = field.form.querySelector(field.focusTargetSelector);
            if (focusTarget) {
                focusTarget.focus();
                return;
            }
        }

        if (field.strategyName === "otp") {
            var otpFirst = field.form.querySelector("[data-auth-otp-target='" + field.input.id + "'] input[data-auth-otp-slot='true']");
            if (otpFirst) {
                otpFirst.focus();
                return;
            }
        }

        field.input.focus();
    }

    function lockFormSubmission(form, event) {
        if (form.dataset.authSubmitting === "true") {
            event.preventDefault();
            return false;
        }

        form.dataset.authSubmitting = "true";
        var submitter = event.submitter;
        var submitButtons = form.querySelectorAll("button[type='submit']");
        var loadedAny = false;

        submitButtons.forEach(function (button) {
            button.setAttribute("disabled", "disabled");
            if (submitter && button !== submitter) {
                return;
            }

            loadedAny = true;
            setButtonLoading(button);
        });

        if (!loadedAny && submitButtons.length > 0) {
            setButtonLoading(submitButtons[0]);
        }

        return true;
    }

    function wireForm(form) {
        var fieldStates = Array.from(form.querySelectorAll("[data-auth-validate]"))
            .map(getFieldState)
            .filter(Boolean);

        if (fieldStates.length === 0) {
            form.addEventListener("submit", function (event) {
                lockFormSubmission(form, event);
            });
            return;
        }

        var formState = {
            submitted: false
        };
        var summary = getSummaryElement(form);
        if (summary && summary.textContent.trim().length === 0) {
            setSummaryMessages(form, []);
        } else if (summary && summary.textContent.trim().length > 0) {
            formState.submitted = true;
        }

        fieldStates.forEach(function (field) {
            var hasServerError = field.errorElement && field.errorElement.textContent.trim().length > 0;
            if (hasServerError) {
                field.touched = true;
                field.blurred = true;
                formState.submitted = true;
                setErrorState(field, field.errorElement.textContent.trim());
            }

            field.input.addEventListener("blur", function () {
                field.touched = true;
                field.blurred = true;
                validateField(field, formState, "blur");
                syncSummaryState(form, fieldStates, formState);
                refreshSubmitState(form, fieldStates);
            });

            field.input.addEventListener("input", function () {
                field.touched = true;
                if (shouldValidateOnInput(field, formState)) {
                    validateField(field, formState, "input");
                }

                var dependentSelector = field.input.getAttribute("data-auth-revalidate-target");
                if (dependentSelector) {
                    var dependentInput = form.querySelector(dependentSelector);
                    var dependentField = fieldStates.find(function (candidate) {
                        return candidate.input === dependentInput;
                    });

                    if (dependentField && (dependentField.touched || formState.submitted)) {
                        validateField(dependentField, formState, "input");
                    }
                }

                syncSummaryState(form, fieldStates, formState);
                refreshSubmitState(form, fieldStates);
            });

            field.input.addEventListener("change", function () {
                field.touched = true;
                validateField(field, formState, "change");
                syncSummaryState(form, fieldStates, formState);
                refreshSubmitState(form, fieldStates);
            });

            field.input.addEventListener("auth:otp-blur", function () {
                field.touched = true;
                field.blurred = true;
                validateField(field, formState, "blur");
                syncSummaryState(form, fieldStates, formState);
                refreshSubmitState(form, fieldStates);
            });
        });

        // Gate the submit button on load: disabled until all required fields are valid.
        refreshSubmitState(form, fieldStates);
        // Re-check shortly after load to catch browser autofill (which may not fire input events).
        window.setTimeout(function () { refreshSubmitState(form, fieldStates); }, 350);
        window.addEventListener("pageshow", function () { refreshSubmitState(form, fieldStates); });

        form.addEventListener("submit", function (event) {
            formState.submitted = true;

            var invalidFields = [];
            fieldStates.forEach(function (field) {
                field.touched = true;
                field.blurred = true;
                var message = validateField(field, formState, "submit");
                if (message) {
                    invalidFields.push(field);
                }
            });

            if (invalidFields.length > 0) {
                event.preventDefault();
                setSummaryMessages(form, ["Please correct the highlighted fields and try again."]);
                focusField(invalidFields[0]);
                return;
            }

            setSummaryMessages(form, []);
            lockFormSubmission(form, event);
        });
    }

    document.querySelectorAll("form[data-auth-form='true']").forEach(wireForm);
})();
