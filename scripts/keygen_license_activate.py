#!/usr/bin/env python3
"""
Keygen (keygen.sh) — validate a license key and register a machine (device).

Credentials are read from the environment (never commit secrets):
  KEYGEN_ACCOUNT_ID   — required
  KEYGEN_API_TOKEN    — required (Bearer token that may create machines)

Optional: copy scripts/keygen.env.example to `.env` in this folder or the repo root
          (see `.gitignore` — `.env` is not tracked).

Requires: pip install requests

Notes:
- Keygen's public docs emphasize POST .../licenses/actions/validate-key for license keys.
- Some cryptographic setups use POST .../licenses/actions/decrypt-key; this script tries
  decrypt-key first, then falls back to validate-key if decrypt-key is not available (404).
- Device activation uses the Keygen **machines** resource (your "device" in the dashboard).
"""

from __future__ import annotations

import hashlib
import json
import os
import secrets
import sys
from pathlib import Path
from typing import Any

import requests

SCRIPT_DIR = Path(__file__).resolve().parent


def _load_dotenv(path: Path) -> None:
    """Minimal .env loader (no python-dotenv dependency). Does not override existing env."""
    if not path.is_file():
        return
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, val = line.partition("=")
        key = key.strip()
        if not key or key in os.environ:
            continue
        val = val.strip()
        if len(val) >= 2 and val[0] == val[-1] and val[0] in "\"'":
            val = val[1:-1]
        os.environ[key] = val


def _ensure_env_loaded() -> None:
    """Load `.env` from script directory, then current working directory."""
    _load_dotenv(SCRIPT_DIR / ".env")
    _load_dotenv(Path.cwd() / ".env")


def _account_id() -> str:
    return os.environ.get("KEYGEN_ACCOUNT_ID", "").strip()


def _api_token() -> str:
    return os.environ.get("KEYGEN_API_TOKEN", "").strip()


def _api_base() -> str:
    base = os.environ.get("KEYGEN_API_BASE", "https://api.keygen.sh/v1").strip().rstrip("/")
    return base or "https://api.keygen.sh/v1"


def _prefer_validate_key_only() -> bool:
    v = os.environ.get("KEYGEN_PREFER_VALIDATE_KEY_ONLY", "").strip().lower()
    return v in ("1", "true", "yes", "on")

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _headers_json() -> dict[str, str]:
    return {
        "Content-Type": "application/vnd.api+json",
        "Accept": "application/vnd.api+json",
    }


def _headers_bearer() -> dict[str, str]:
    h = _headers_json()
    h["Authorization"] = f"Bearer {_api_token()}"
    return h


def _print_errors(resp: requests.Response) -> None:
    try:
        body = resp.json()
    except Exception:
        print(f"HTTP {resp.status_code}: {resp.text[:2000]}", file=sys.stderr)
        return
    errs = body.get("errors")
    if errs:
        for e in errs:
            title = e.get("title", "Error")
            detail = e.get("detail", "")
            print(f"  API error: {title} — {detail}", file=sys.stderr)
    else:
        print(json.dumps(body, indent=2)[:4000], file=sys.stderr)


def _machine_fingerprint(device_name: str) -> str:
    """Stable 64-char hex fingerprint for this script run + device name."""
    raw = hashlib.sha256(device_name.strip().encode("utf-8")).hexdigest()
    return raw[:64]


def decrypt_or_validate_license_key(
    session: requests.Session, license_key: str, validation_fingerprint: str
) -> dict[str, Any]:
    """
    Call decrypt-key (then validate-key fallback). Returns parsed JSON on success.

    Keygen's documented validate-key body includes meta.scope.fingerprint (see
    https://keygen.sh/docs/api/licenses ). We send the same shape for decrypt-key.
    """
    account = _account_id()
    if not account:
        raise ValueError("Set KEYGEN_ACCOUNT_ID (environment or .env file).")

    payload = {
        "meta": {
            "key": license_key.strip(),
            "scope": {"fingerprint": validation_fingerprint},
        }
    }

    base = _api_base()
    if not _prefer_validate_key_only():
        url = f"{base}/accounts/{account}/licenses/actions/decrypt-key"
        r = session.post(url, headers=_headers_json(), json=payload, timeout=60)
        if r.status_code == 200:
            print("[OK] License key accepted via decrypt-key endpoint.")
            return r.json()
        if r.status_code != 404:
            print(f"[FAIL] decrypt-key HTTP {r.status_code}", file=sys.stderr)
            _print_errors(r)
            r.raise_for_status()
        print(
            "[INFO] decrypt-key not available (404). Falling back to validate-key "
            "(documented in https://keygen.sh/docs/validating-licenses/)."
        )

    url = f"{base}/accounts/{account}/licenses/actions/validate-key"
    r = session.post(url, headers=_headers_json(), json=payload, timeout=60)
    if r.status_code != 200:
        print(f"[FAIL] validate-key HTTP {r.status_code}", file=sys.stderr)
        _print_errors(r)
        r.raise_for_status()
    print("[OK] License key validated via validate-key endpoint.")
    return r.json()


def license_is_active(body: dict[str, Any]) -> tuple[bool, str]:
    """Use JSON:API license object + meta.valid when present."""
    meta = body.get("meta") or {}
    if "valid" in meta:
        v = bool(meta.get("valid"))
        return v, meta.get("detail") or meta.get("code") or ""

    data = body.get("data")
    if isinstance(data, dict):
        attrs = data.get("attributes") or {}
        status = (attrs.get("status") or "").upper()
        if status == "ACTIVE":
            return True, "status=ACTIVE"
        return False, f"status={status or 'unknown'}"

    return False, "Could not determine license status from response"


def create_machine(
    session: requests.Session,
    license_id: str,
    device_name: str,
    fingerprint: str,
) -> dict[str, Any]:
    """POST /accounts/{account}/machines — Keygen names devices 'machines'."""
    account = _account_id()
    url = f"{_api_base()}/accounts/{account}/machines"
    import platform

    payload = {
        "data": {
            "type": "machines",
            "attributes": {
                "fingerprint": fingerprint,
                "name": device_name.strip(),
                "platform": platform.system() or "unknown",
            },
            "relationships": {
                "license": {"data": {"type": "licenses", "id": license_id}},
            },
        }
    }
    r = session.post(url, headers=_headers_bearer(), json=payload, timeout=60)
    if r.status_code not in (200, 201):
        print(f"[FAIL] Create machine HTTP {r.status_code}", file=sys.stderr)
        _print_errors(r)
        r.raise_for_status()
    return r.json()


def main() -> int:
    _ensure_env_loaded()

    if not _api_token():
        print(
            "Set KEYGEN_API_TOKEN in the environment or a .env file "
            "(Bearer token with permission to create machines). See scripts/keygen.env.example.",
            file=sys.stderr,
        )
        return 1

    if not _account_id():
        print(
            "Set KEYGEN_ACCOUNT_ID in the environment or a .env file. See scripts/keygen.env.example.",
            file=sys.stderr,
        )
        return 1

    license_key = input("Enter license key: ").strip()
    if not license_key:
        print("Empty license key.", file=sys.stderr)
        return 1

    # One-time fingerprint for validate-key / decrypt-key (per Keygen docs)
    validation_fp = secrets.token_hex(32)

    with requests.Session() as session:
        try:
            body = decrypt_or_validate_license_key(session, license_key, validation_fp)
        except requests.HTTPError:
            return 1
        except Exception as ex:
            print(f"[FAIL] {ex}", file=sys.stderr)
            return 1

        ok, reason = license_is_active(body)
        if not ok:
            print(f"[FAIL] License is not active or invalid: {reason}")
            return 1
        print(f"[OK] License check passed ({reason}).")

        data = body.get("data")
        if not isinstance(data, dict) or not data.get("id"):
            print(
                "[FAIL] Response did not include data.id (license id). "
                "Cannot attach a machine without it.",
                file=sys.stderr,
            )
            print(json.dumps(body, indent=2)[:4000])
            return 1

        license_id = str(data["id"])
        device_name = input("Enter a name for this device (machine): ").strip()
        if not device_name:
            print("Empty device name.", file=sys.stderr)
            return 1

        fp = _machine_fingerprint(device_name)
        try:
            m = create_machine(session, license_id, device_name, fp)
        except requests.HTTPError:
            return 1
        except Exception as ex:
            print(f"[FAIL] {ex}", file=sys.stderr)
            return 1

        mid = (m.get("data") or {}).get("id", "?")
        print(f"[OK] Machine registered successfully (id={mid}).")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
