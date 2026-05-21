# Admin Security Model

SphereNet has several operator surfaces. Treat every one of them as an admin
control plane, not as a public gameplay endpoint.

## Surfaces

- UO client port: public gameplay traffic. Account creation is controlled by
  `AccApp` in `sphere.ini`.
- Telnet admin: binds to loopback and starts only when `AdminPassword` is
  non-empty. Empty passwords fail closed.
- Web status: loopback-only status JSON intended for local tooling. Keep it
  behind a trusted host boundary.
- Web panel: bearer-token admin API served by `SphereNet.Host`. Use a reverse
  proxy with TLS if it is exposed beyond localhost.
- IPC named pipe: local-trust channel between host and managed server. Any local
  process with pipe access should be considered trusted.
- Headless stdin: direct process console commands. Only run the server under an
  account that trusted operators can access.

## Deployment Checklist

- Set a non-empty `AdminPassword` before enabling telnet or panel access.
- Keep `AccApp=0` on production shards unless open account creation is intended.
- Bind admin surfaces to localhost or protect them with a trusted reverse proxy.
- Do not expose named pipes, server stdin, or raw panel HTTP to untrusted users.
- Rotate the panel password after setup and after any operator departure.
