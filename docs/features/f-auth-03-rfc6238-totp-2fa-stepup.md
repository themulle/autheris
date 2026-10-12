# F-AUTH-03: RFC 6238 TOTP Two-Factor Authentication & HitL Step-Up Engine

## 1. Overview & Business Value

The **TOTP Two-Factor Authentication Engine** implements standard RFC 6238 Time-Based One-Time Passwords (TOTP) to secure privileged administrative operations, Human-in-the-Loop (HitL) approvals, and high-impact access grants.

### Compatibility & Security Guarantees:
- **Universal Authenticator App Support:** Fully compatible with Microsoft Authenticator, Google Authenticator, 1Password, and Apple Passwords using standard `otpauth://` URI schemas.
- **HMAC-SHA1 & 30-Second Windows:** Computes 6-digit codes with $\pm 1$ time-step window skew tolerance (allowing for up to 30 seconds of client-server clock drift).
- **Single-Use Replay Protection:** Used TOTP tokens are cached in memory for the duration of their valid time step, strictly preventing replay attacks.
- **Cryptographic Confirmation Tokens:** Validating a TOTP code during a Step-Up challenge produces a short-lived (5-minute TTL), cryptographically signed `confirmationToken` required to execute two-phase access modifications.

## 2. API Endpoints

```http
POST /api/v1/auth/2fa/enroll
Authorization: Bearer <jwt-or-api-key>
Content-Type: application/json

{}
```

Response:
```json
{
  "secret": "JBSWY3DPEHPK3PXP",
  "qrCodeUri": "otpauth://totp/Autheris:admin%40company.com?secret=JBSWY3DPEHPK3PXP&issuer=Autheris&period=30&digits=6",
  "periodSeconds": 30,
  "digits": 6
}
```

```http
POST /api/v1/auth/2fa/verify
Authorization: Bearer <jwt-or-api-key>
Content-Type: application/json

{
  "code": "482910"
}
```

## 3. Implementation Details

- **Service:** `TotpVerificationService` in `src/Autheris.Application/Security/TotpVerificationService.cs`.
- **Endpoints:** Mapped in `src/Autheris.Api/Endpoints/TwoFactorAuthEndpoints.cs`.
