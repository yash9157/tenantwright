(function () {
    var form = document.getElementById("passkey-complete-form");
    if (!form) {
        return;
    }

    var optionsElement = document.getElementById("passkey-options");
    var responseInput = document.getElementById("passkey-response");
    var status = document.getElementById("passkey-status");
    var spinner = document.getElementById("passkey-spinner");
    var errorIcon = document.getElementById("passkey-icon-error");
    var retryGroup = document.getElementById("passkey-retry-group");
    var retry = document.getElementById("passkey-retry");
    var enrolling = form.dataset.enrolling === "true";

    function decode(value) {
        var base64 = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
        var binary = atob(base64);
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
        }
        return bytes;
    }

    function encode(value) {
        if (!value) {
            return null;
        }
        var bytes = new Uint8Array(value);
        var binary = "";
        bytes.forEach(function (byte) {
            binary += String.fromCharCode(byte);
        });
        return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
    }

    function prepare(source) {
        source.challenge = decode(source.challenge);
        if (source.user && source.user.id) {
            source.user.id = decode(source.user.id);
        }
        (source.excludeCredentials || []).forEach(function (descriptor) {
            descriptor.id = decode(descriptor.id);
        });
        (source.allowCredentials || []).forEach(function (descriptor) {
            descriptor.id = decode(descriptor.id);
        });
        return source;
    }

    function serialize(credential) {
        return {
            id: credential.id,
            rawId: encode(credential.rawId),
            type: credential.type,
            authenticatorAttachment: credential.authenticatorAttachment,
            response: enrolling ? {
                clientDataJSON: encode(credential.response.clientDataJSON),
                attestationObject: encode(credential.response.attestationObject),
                transports: credential.response.getTransports ? credential.response.getTransports() : []
            } : {
                clientDataJSON: encode(credential.response.clientDataJSON),
                authenticatorData: encode(credential.response.authenticatorData),
                signature: encode(credential.response.signature),
                userHandle: encode(credential.response.userHandle)
            },
            clientExtensionResults: credential.getClientExtensionResults()
        };
    }

    function showWaiting() {
        retryGroup.classList.add("hidden");
        errorIcon.classList.add("hidden");
        spinner.classList.remove("hidden");
        status.classList.remove("text-warning");
        status.classList.add("text-on-surface-soft");
        status.textContent = "Waiting for your authenticator… check for a prompt from Windows Hello, Touch ID, or your security key.";
    }

    function showFailure(message) {
        spinner.classList.add("hidden");
        errorIcon.classList.remove("hidden");
        status.classList.remove("text-on-surface-soft");
        status.classList.add("text-warning");
        status.textContent = message;
        retryGroup.classList.remove("hidden");
    }

    function run() {
        showWaiting();

        var ceremony = window.PublicKeyCredential
            ? Promise.resolve()
            : Promise.reject(new Error("Passkeys are not supported by this browser."));

        ceremony
            .then(function () {
                var publicKey = prepare(JSON.parse(optionsElement.textContent));
                return enrolling
                    ? navigator.credentials.create({ publicKey: publicKey })
                    : navigator.credentials.get({ publicKey: publicKey });
            })
            .then(function (credential) {
                if (!credential) {
                    throw new Error("No authenticator response was returned.");
                }
                responseInput.value = JSON.stringify(serialize(credential));
                form.submit();
            })
            .catch(function (error) {
                showFailure(error && error.name === "NotAllowedError"
                    ? "The passkey request was cancelled or timed out. No sign-in was completed."
                    : (error && error.message ? error.message : "The passkey request failed."));
            });
    }

    retry.addEventListener("click", run);
    run();
})();
