# Validation

Date: October 26, 2026.

## Automated checks completed

- Plugin Release build: .NET 10, Jellyfin.Controller and Jellyfin.Model 12.0.0; zero compiler warnings or errors.
- Server suite: **29 passed, 0 failed**. Includes ASP.NET HTTP routing/authentication, current-user library lookups, capability discovery with no upstream call, missing configuration, exact episode-ID matching, specials, alternate ordering, combined files, manual overrides, conflicting series IDs, air-date disagreements, pagination, comment ownership for replies, request coalescing, credential cache partitioning, rate-limit cooldown and upstream error/payload handling.
- Android suite: **7 passed, 0 failed**. Includes supported capabilities, plugin removal between checks, HTTP errors, incompatible/unconfigured/malformed capabilities, spoiler flags and inline tags, and versioned comments/replies requests through the existing Jellyfin SDK client.
- The Android integration patch passes `git apply --reverse --check` against the modified checkout. The checkout's normal index was left unchanged.
- The Android build configuration and ProGuard rules were preserved. The generated stats BuildConfig has `DEBUG=false`, `DEVELOPMENT=false`, `BUILD_TYPE=stats`, version `0.20.0-stats.13`, and application ID `org.jellyfin.androidtv.debug`.
- The exact requested stats command completed successfully: `./gradlew.bat :app:assembleStats "-Pjellyfin.version=0.20.0-stats.13" --console=plain`. R8 minification and resource shrinking completed. Total build time was 11 minutes 39 seconds.
- Final APK: **29,986,203 bytes** (29.99 MB decimal), up 30,494 bytes from the previous 29,955,709-byte stats APK. Includes `arm64-v8a`, `armeabi-v7a`, `x86` and `x86_64`.
- APK signature verification passed. The certificate SHA-256 fingerprint matches the previous stats APK: `5825be346f03aa4d364970042037075f8244ccfee348f39c857373cec7f192eb`.
- APK manifest inspection confirms package `org.jellyfin.androidtv.debug`, version code `200013`, version name `0.20.0-stats.13`, and absence of the debuggable application flag. The R8 mapping retains the comments API, capability serializer and dialog implementations.

Machine-readable server and Android results are in `validation/`. The API tests use fake upstream responses, not a real Trakt API credential.

## Live validation boundary

No Jellyfin installation was changed, no APK was installed on a TV, and no real Trakt comments were fetched in this workspace. Plugin loading in your running server, API access with your own Client ID, and physical remote/focus behavior still need a live check. The plugin and APK are provided as installable artifacts for that check.

The Android app makes the integration optional: capability discovery is asynchronous, fails closed, and is repeated at selection. Removal after a discussion has already opened is handled by the dialog's error state; it does not affect playback.
