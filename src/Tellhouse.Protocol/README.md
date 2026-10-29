# Tellhouse.Protocol

Protocol and crypto core for Tellhouse: the canonical
packet envelope, household and member keypairs, sealing and
opening, per connection signing, and the session open challenge.

The contract is CONTRACT.md in this directory. It is frozen;
other projects code against CONTRACT.md and the shared test
vectors at vectors/test-vectors.json, not against this library's
internals.

Layout, from the repository root:

- src/Tellhouse.Protocol: the reference implementation, .NET 10,
  base class library only, no NuGet packages, so it restores and
  builds offline.
- tests/Tellhouse.Protocol.Tests: the check suite, a console
  runner. Run it with
  dotnet run --project tests/Tellhouse.Protocol.Tests
  from the repository root.
- tools/Tellhouse.Protocol.VectorGen: regenerates the test
  vectors deterministically (signatures excepted, verification
  only by design).
- vectors/test-vectors.json: the shared vectors.

One household, two members, three packet types. The envelope
carries exactly the relay-visible metadata list and never a
material class.
