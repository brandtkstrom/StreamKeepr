# Security and project scope

StreamKeepr is a personal project intended for private home use. It is not designed, tested, or supported for public hosting, multi-tenant use, or third-party distribution. The MIT license permits reuse, but the project's intent and support scope are private.

## Supported posture

- Run StreamKeepr on a trusted private home network or a single-operator host.
- Keep the recordings volume, secret files, and Data Protection keys private.
- If you expose the app beyond localhost, put it behind your own HTTPS reverse proxy and access controls, and treat that exposure as unsupported.
- Do not run the `Mock` environment on a production host. It registers a fake admin login by design.

## No security support

This project does not provide a security response process, warranty, or support commitment. It is provided under the MIT license "as is", without warranty of any kind. You are responsible for the network, host, TLS, secrets, backups, and access controls around your deployment.

## Secrets

Use user-secrets for local development and the documented `/run/secrets` mounts for containers. Never commit, log, or bake into an image the Twitch client secret, the recorder token, a chat token, or any other credential. The application is designed to keep the media surface public only within the trusted deployment described above.

## Reporting

If you believe you have found a vulnerability, do not post secrets or personal data in a public issue. Use GitHub's private security advisory flow for this repository if it is available; otherwise contact the repository owner directly.
