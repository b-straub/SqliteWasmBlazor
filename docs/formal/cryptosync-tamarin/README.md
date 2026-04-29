# CryptoSync Tamarin Models

This directory splits the CryptoSync protocol into small symbolic Tamarin
models. The goal is to keep each layer fast to prove and easy to compare with
the implementation.

## Layers

1. `01-invitation-control-plane.spthy`
   Admin-signed invitation bundle, invitation response channel, contact-signed
   response identity.
2. `02-group-key-distribution.spthy`
   Admin-signed ShareTarget credentials, CEK distribution, one bounded
   revocation rotation.
3. `03-delta-data-plane.spthy`
   DeltaEnvelope outer signature, per-group batch signature, row AEAD with
   `groupContext:keyVersion` AAD, receiver acceptance provenance.
4. `04-relay-whitelist-cursor.spthy`
   Whitelist update authority, sender/receiver relay authorization, revoked
   read grace, monotonic cursor acceptance.

Run one model:

```sh
tamarin-prover --prove docs/formal/cryptosync-tamarin/01-invitation-control-plane.spthy
```

Run all models:

```sh
for f in docs/formal/cryptosync-tamarin/*.spthy; do
  tamarin-prover --prove "$f"
done
```

These are symbolic protocol models. They intentionally trust the primitive
implementations and leave SQL merge correctness, UI behavior, browser storage,
and denial-of-service economics to tests, audits, and implementation review.

