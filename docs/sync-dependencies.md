# Stage 01 dependency verification

Protocol types introduce no runtime packages. Existing manifests remain unchanged until native integration can be validated. Exact candidate versions below are pins for that validation, not claims of successful Android/iOS builds.

| Dependency | Exact version | Evidence / outstanding work |
| --- | --- | --- |
| React Native | 0.87.1 | Existing package/lock; New Architecture enabled. |
| SignalR JS and MessagePack JS | 10.0.11 | Existing pins. v1 specifies JSON; actual authenticated RN WebSocket remains an integration check. |
| react-native-sqlite-storage | 6.0.1 | Existing pin and Android patch retained; transaction completion needs stage 03 validation. |
| react-native-quick-crypto | 1.1.8 | Official release: New Architecture, RN >=0.75; peers Nitro >=0.31.2, quick-base64 >=3.0.0. |
| react-native-nitro-modules | 0.33.2 | Exact dev version in Quick Crypto 1.1.8; candidate for this app. |
| react-native-quick-base64 | 3.0.0 | Candidate satisfying peer floor; native graph unverified. |
| react-native-keychain | 10.0.0 | Release min Android SDK 23; app minSdk 24. Native behavior unverified. |
| RabbitMQ.Client | 7.2.2 | Published net8.0 target; restore/build and broker integration pending. |
| Built-in .NET AesGcm/ECDsa | net8.0 / runtime 8.0.30 | Executed installed Linux implementations in cross-runtime probe. |
| Argon2id | Quick Crypto 1.1.8 | Candidate client derivation; no server Argon2 dependency since server does not receive password. Native API/performance unverified. |

Official sources checked 2026-10-25:

- [Quick Crypto compatibility](https://margelo.github.io/react-native-quick-crypto/docs/introduction/releases)
- [Quick Crypto 1.1.8 package](https://github.com/margelo/react-native-quick-crypto/blob/v1.1.8/packages/react-native-quick-crypto/package.json)
- [Quick Crypto 1.1.8 podspec](https://github.com/margelo/react-native-quick-crypto/blob/v1.1.8/packages/react-native-quick-crypto/QuickCrypto.podspec)
- [Keychain 10.0.0](https://github.com/oblador/react-native-keychain/releases/tag/v10.0.0)
- [RabbitMQ.Client 7.2.2 frameworks](https://www.nuget.org/packages/RabbitMQ.Client/7.2.2)

Current app Podfile pins iOS 15.6; Quick Crypto derives its minimum from React Native's CocoaPods helper. Resolve the full pods graph before declaring compatibility. This change does not silently raise native deployment targets or install unverified packages.

## Executed checks

All passed in this workspace:

1. Node 22.23.2 reference fixture creation, decimal sequence boundaries and 32KiB request budget.
2. .NET 8 contract compilation and executable assertions for discriminated outcomes, string enums, additive fields, AES-GCM cross-runtime bytes/AAD/decryption/tamper rejection, and ES256/P1363 verification/signing.
3. Node verification of the .NET signature and parity for 51 DTO property names, types and nullability, all enum values and protocol constants; actual four outcome JSON fixtures match TypeScript-checked examples.
4. Targeted strict TypeScript compilation using TypeScript 6.0.3, including the outcome fixtures.
5. Ordered, completed sync position/boundary exchanges for 301 maximum-length stream identifiers, with request and response byte caps; enum rejection for case, whitespace, combinations, numbers, null and unknown names.
6. Exact recovery AAD cross-runtime bytes and AES-GCM decrypt/tamper test with a fixed derived-key reference.
7. Full Services/Services.csproj build: zero errors; warnings remain in existing code/dependencies.

Reproduce from sibling repositories:

```sh
cd ChatAppClient
node --experimental-strip-types scripts/check-sync-contracts.mjs prepare /tmp/chat-sync-node-fixture.json
cd ../ChatAPI
dotnet run --project tests/SyncContracts/SyncContracts.csproj -- /tmp/chat-sync-node-fixture.json /tmp/chat-sync-dotnet-result.json
cd ../ChatAppClient
node --experimental-strip-types scripts/check-sync-contracts.mjs verify /tmp/chat-sync-node-fixture.json /tmp/chat-sync-dotnet-result.json
npm exec --yes --package=typescript@6.0.3 -- tsc --ignoreConfig --strict --noEmit --target ES2020 --module ESNext --moduleResolution bundler --skipLibCheck src/Types/Sync/index.ts src/Constants/SyncProtocol.ts scripts/sync-contract-fixtures.ts
```

The test project links actual production DTO files without restoring unrelated application dependencies. The generated keys are public test material. Node crypto is a reference implementation, not proof of Quick Crypto phone compatibility.

Outstanding stage-01 acceptance evidence: run equivalent vectors on Android and iOS with the exact candidate dependency graph; validate SPKI/PKCS#8 imports, JWS rejection cases, Argon2id cost, Keychain/Keystore locking/persistence, SQLite commit callbacks and authenticated SignalR. This environment lacks Android SDK/Java and Xcode. Native compatibility remains explicitly unproven; no later plan has been started.
