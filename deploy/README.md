# Deploying Sovereign behind a login

The playground is authenticated by the service, not by the browser. A static host cannot
refuse a request, so enforcement has to live in the .NET service, which can return 401 or a
redirect. That means the service has to be reachable by a browser, behind TLS, with the built
playground served from `SOVEREIGN_WEB_ROOT`.

Pick **Caddy** (automatic certificates, smallest file) or **nginx** (if you already run it).
Both keep the service on loopback; the proxy is the only public listener.

## Before either route

```bash
# 1. Build the playground once; the service serves this directory.
npm ci
npm run build
sudo mkdir -p /opt/sovereign && sudo cp -r . /opt/sovereign
cd /opt/sovereign
sudo dotnet build Solution1.sln -c Release

# 2. Generate the two secrets. The JWT secret signs tokens; the password hash
#    is the PBKDF2 digest, never the password itself.
sudo mkdir -p /etc/sovereign
printf 'SOVEREIGN_JWT_SECRET=%s\n' "$(openssl rand -base64 32)" | sudo tee /etc/sovereign/secrets.env
echo "pbkdf2 digest: $(dotnet run --project Sovereign.Host -c Release --no-build -- passwd 'yourpassword')"
```

Add the digest to `/etc/sovereign/secrets.env`, then lock the file down:

```bash
printf 'SOVEREIGN_ADMIN_PASSWORD_HASH=pbkdf2$210000$...\n' | sudo tee -a /etc/sovereign/secrets.env
sudo chown root:root /etc/sovereign/secrets.env
sudo chmod 600 /etc/sovereign/secrets.env
```

The password itself is never written to disk anywhere in this project. If you lose it, hash a
new one with the `passwd` command; there is no recovery path, by design.

## Caddy

```bash
sudo cp deploy/Caddyfile /etc/caddy/Caddyfile
# edit the domain on the first line
sudo caddy validate --config /etc/caddy/Caddyfile
sudo systemctl reload caddy
```

Caddy provisions and renews the certificate on its own. It must be able to reach port 80 for the
ACME challenge, and the domain must already point at the machine.

## nginx

```bash
sudo cp deploy/nginx-sovereign.conf /etc/nginx/sites-available/sovereign
sudo ln -s /etc/nginx/sites-available/sovereign /etc/nginx/sites-enabled/sovereign
# edit server_name and the certificate paths
sudo certbot --nginx -d sovereign.example.com
sudo nginx -t && sudo systemctl reload nginx
```

## Service

```bash
sudo useradd --system --no-create-home sovereign
sudo cp deploy/sovereign.service /etc/systemd/system/sovereign.service
sudo systemctl daemon-reload
sudo systemctl enable --now sovereign
sudo systemctl status sovereign
```

## Verify it actually gates

This is the check that matters, and it is worth running from a browser on a network that is
not the server:

```bash
# Unauthenticated: a redirect to /login, never the application.
curl -sSI https://sovereign.example.com/ | head -1
#   HTTP/2 302      (and a Location: /login header)

# The API refuses without a credential.
curl -sS https://sovereign.example.com/api/status
#   {"error":"A valid, unexpired playground JWT is required."}

# The sign-in page is reachable, otherwise nobody could sign in.
curl -sS -o /dev/null -w '%{http_code}\n' https://sovereign.example.com/login
#   200

# The session cookie must be Secure, HttpOnly and SameSite=Strict.
curl -sSI -X POST https://sovereign.example.com/auth/login \
  -d 'username=admin&password=yourpassword' | grep -i set-cookie
#   sovereign_session=...; secure; samesite=strict; httponly
```

If `set-cookie` is missing `secure`, the service is not seeing `X-Forwarded-Proto` and the
proxy is not passing it through. If `/` returns 200 instead of 302, `SOVEREIGN_WEB_ROOT` is not
set, so the service is API-only and the app is being served from somewhere else.

Then in a browser: open the URL, confirm you are asked to sign in, sign in with the password,
confirm the playground loads, and confirm that `/api/status` now reports authenticated.

## Notes and limits

- **Keep the service on loopback.** `SOVEREIGN_BIND` defaults to `127.0.0.1`. The forwarded
  headers are trusted without a known-proxy allowlist, which is safe only while the port is
  unreachable from outside. Widening the bind to `0.0.0.0` would let any client forge
  `X-Forwarded-Proto`.
- **TLS is not optional.** Without it the password crosses the network in cleartext. The cookie
  gains `Secure` only when the request arrives over HTTPS.
- **Tokens last 15 minutes** and there is no refresh. An active user signs in again when it
  expires. There is no server-side revocation, so signing out clears the cookie but does not
  invalidate the token until it expires.
- **Caddy's access log already redacts** `Cookie`, `Set-Cookie` and `Authorization`, and the
  `SAMLResponse` travels in a POST body, which is never logged. No extra filter is needed.
- **One operator.** Documents, audit and sessions are keyed to a single subject. This is not a
  multitenant service and should not be exposed as one.
- The configs here are written against Caddy 2 and nginx 1.24+ but have **not** been machine
  validated in this repository. Run `caddy validate` or `nginx -t` before reloading, and run
  the verification commands above rather than assuming the gate is in place.
