#!/usr/bin/env python3
"""Operator helper for a fresh Tellhouse deployment.

Prints the relay registry hashes a fresh deployment needs, so
the operator can paste them into the relay service's
environment (RELAY_NODE_TOKEN_SHA256, RELAY_LISA_TOKEN_SHA256,
RELAY_DANIEL_TOKEN_SHA256 in render.yaml). The derivation is
the product's published door credential rule, the same one the
test drivers use: a member's connection credential is HKDF
(HMAC-SHA-256) over the invite code the node issued, salted
with the member's door fingerprint, info "tellhouse-door-v1";
the relay registry carries the hex SHA-256 of that credential,
never the credential itself. For the node's own connection the
registry carries the hex SHA-256 of the node token the
operator chose.

Usage:
  credential_hash.py --token "<fresh node token>"
  credential_hash.py --code "<invite code>" --fingerprint "<hex from the stand-in /status>"
  credential_hash.py --code "<invite code>" --status-url "https://tellhouse-lisa.onrender.com/status"

Nothing here is secret: the inputs are the operator's own
fresh values, and the outputs are hashes.
"""
import argparse
import hashlib
import hmac as hmac_mod
import json
import sys
import urllib.request


def hkdf_hex(ikm, salt, info, length=32):
    prk = hmac_mod.new(salt, ikm, hashlib.sha256).digest()
    out, t, counter = b"", b"", 1
    while len(out) < length:
        t = hmac_mod.new(prk, t + info + bytes([counter]), hashlib.sha256).digest()
        out += t
        counter += 1
    return out[:length].hex()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--token", help="the fresh node token (prints its SHA-256)")
    ap.add_argument("--code", help="the invite code the node issued to one stand-in")
    ap.add_argument("--fingerprint", help="the stand-in's door fingerprint (hex)")
    ap.add_argument("--status-url", help="the stand-in's /status URL, to read its fingerprint")
    args = ap.parse_args()
    if args.token:
        print(hashlib.sha256(args.token.encode()).hexdigest())
        return 0
    if args.code:
        fingerprint = args.fingerprint
        if not fingerprint and args.status_url:
            with urllib.request.urlopen(args.status_url, timeout=30) as resp:
                fingerprint = json.loads(resp.read().decode())["fingerprint"]
        if not fingerprint:
            print("a fingerprint is required (pass --fingerprint or --status-url)",
                  file=sys.stderr)
            return 2
        credential = hkdf_hex(args.code.encode(),
                              ("invite:" + fingerprint).encode(),
                              b"tellhouse-door-v1")
        print(hashlib.sha256(credential.encode()).hexdigest())
        return 0
    ap.print_usage(sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
