(function () {
    function registerAlpineComponents() {
        if (!window.Alpine) {
            return;
        }

        window.Alpine.data("authPasswordField", function () {
            return {
                visible: false,
                get passwordType() {
                    return this.visible ? "text" : "password";
                },
                get toggleLabel() {
                    return this.visible ? "Hide password" : "Show password";
                },
                // CSP-build-safe getter (no inline `!` expressions allowed in markup).
                get concealed() {
                    return !this.visible;
                },
                toggle() {
                    this.visible = !this.visible;
                }
            };
        });

        // Resend cooldown: disables a link/button and counts down "Resend in 45s".
        // The deadline persists in sessionStorage so navigation can't bypass it.
        window.Alpine.data("authCooldown", function () {
            return {
                remaining: 0,
                seconds: 45,
                key: "auth.cooldown",
                idleLabel: "Resend",
                timer: null,
                init() {
                    this.seconds = Number(this.$el.dataset.cooldownSeconds) || 45;
                    this.key = this.$el.dataset.cooldownKey || "auth.cooldown";
                    this.idleLabel = this.$el.dataset.cooldownLabel || "Resend";
                    this.sync();
                },
                sync() {
                    var until = Number(window.sessionStorage.getItem(this.key) || 0);
                    var left = Math.ceil((until - Date.now()) / 1000);
                    this.remaining = left > 0 ? left : 0;
                    this.schedule();
                },
                schedule() {
                    if (this.timer) {
                        window.clearInterval(this.timer);
                        this.timer = null;
                    }
                    if (this.remaining <= 0) {
                        return;
                    }
                    var self = this;
                    this.timer = window.setInterval(function () {
                        self.remaining -= 1;
                        if (self.remaining <= 0) {
                            self.remaining = 0;
                            window.clearInterval(self.timer);
                            self.timer = null;
                        }
                    }, 1000);
                },
                // Call from the trigger; returns true if the action may proceed.
                start() {
                    if (this.remaining > 0) {
                        return false;
                    }
                    window.sessionStorage.setItem(this.key, String(Date.now() + this.seconds * 1000));
                    this.sync();
                    return true;
                },
                get active() {
                    return this.remaining > 0;
                },
                get idle() {
                    return this.remaining <= 0;
                },
                get label() {
                    return this.remaining > 0 ? ("Resend in " + this.remaining + "s") : this.idleLabel;
                }
            };
        });
    }

    function syncOtpValue(container, targetInput) {
        var slots = container.querySelectorAll("input[data-auth-otp-slot='true']");
        var value = "";
        slots.forEach(function (slot) {
            value += (slot.value || "").trim();
        });

        targetInput.value = value;
        targetInput.dispatchEvent(new Event("input", { bubbles: true }));
        targetInput.dispatchEvent(new Event("change", { bubbles: true }));
    }

    function wireOtpInputs() {
        var containers = document.querySelectorAll("[data-auth-otp='true']");
        containers.forEach(function (container) {
            var targetId = container.getAttribute("data-auth-otp-target");
            if (!targetId) {
                return;
            }

            var targetInput = document.getElementById(targetId);
            if (!targetInput) {
                return;
            }

            var otpInputs = container.querySelectorAll("input[data-auth-otp-slot='true']");
            otpInputs.forEach(function (input, idx) {
                input.addEventListener("input", function () {
                    if (input.value.length > 1) {
                        input.value = input.value.slice(-1);
                    }

                    input.value = input.value.replace(/\D/g, "");

                    if (input.value && idx < otpInputs.length - 1) {
                        otpInputs[idx + 1].focus();
                    }

                    syncOtpValue(container, targetInput);
                });

                input.addEventListener("keydown", function (event) {
                    if (event.key === "Backspace" && !input.value && idx > 0) {
                        otpInputs[idx - 1].focus();
                    }
                });

                input.addEventListener("blur", function () {
                    window.setTimeout(function () {
                        if (!container.contains(document.activeElement)) {
                            targetInput.dispatchEvent(new CustomEvent("auth:otp-blur", { bubbles: true }));
                        }
                    }, 0);
                });

                input.addEventListener("paste", function (event) {
                    var text = (event.clipboardData || window.clipboardData).getData("text");
                    if (!text) {
                        return;
                    }

                    event.preventDefault();
                    var sanitized = text.replace(/\D/g, "").slice(0, otpInputs.length);
                    for (var i = 0; i < otpInputs.length; i++) {
                        otpInputs[i].value = sanitized[i] || "";
                    }

                    syncOtpValue(container, targetInput);
                    if (sanitized.length < otpInputs.length) {
                        otpInputs[sanitized.length].focus();
                    } else {
                        otpInputs[otpInputs.length - 1].focus();
                    }
                });
            });
        });
    }

    if (window.Alpine) {
        registerAlpineComponents();
    } else {
        document.addEventListener("alpine:init", registerAlpineComponents, { once: true });
    }

    wireOtpInputs();
})();
