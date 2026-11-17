# Security Policy

## Supported versions

Vertex is pre-alpha. Only the latest commit on `main` and the latest release receive security fixes.

## Reporting a vulnerability

**Please don't report security vulnerabilities in public issues, discussions or pull requests.**

Report them privately through GitHub: open the repository's **Security** tab and choose
[**Report a vulnerability**](https://github.com/EntexInteractive/Vertex/security/advisories/new). Include:

- What the vulnerability is and which component it affects (server, CLI, agent, web UI)
- Steps to reproduce it, or a proof of concept
- The impact as you understand it, such as data exposure, authentication bypass or denial of service

You'll get an acknowledgement within a few days. We'll keep you updated while we work on a fix and credit you in the
advisory unless you'd rather stay anonymous. Please give us a reasonable amount of time to release a fix before you
disclose the issue publicly.

## Deployment notes

- The server takes all credentials from environment variables. Never bake them into images or commit them.
- The development defaults in `docker-compose.yml` and `.env.example` are for local use only. Change every password
  and key before exposing a server to a network.
- Infrastructure ports in the compose file are bound to `127.0.0.1`. Keep PostgreSQL, Redis and S3 off the public
  internet.
