# Validation

Date: October 26, 2026. Plugin version: **0.2.0.0**.

## Automated checks completed

- Plugin Release build: .NET 10, Jellyfin.Controller and Jellyfin.Model 12.0.0; zero compiler warnings or errors.
- Server suite: **42 passed, 0 failed**. Includes ASP.NET HTTP routing/authentication, current-user library lookups, capabilities, exact movie/show/episode matching, conflicting and ambiguous IDs, specials, alternate ordering, combined files, overrides, pagination, replies ownership by both media type and ID, caching, credential partitioning, rate limits and upstream failure handling. Catalog validation verifies the new ZIP plus retained historical package checksums.
- Android suites: **13 passed, 0 failed** (9 discussion/API tests and 4 episode section/cast tests). Includes plugin removal, old episode-only capabilities, new movie/series capabilities, unsupported item types, malformed responses, spoofed plugin identity, spoilers and inline tags, pagination, exact row order for either asynchronous arrival order, missing optional rows, cast deduplication, guest separation and fallback episode cast metadata.
- The Android integration patch passes `git apply --reverse --check` against the modified checkout. The checkout's normal index was left unchanged.
- The Android build configuration and ProGuard rules were preserved. The generated stats BuildConfig has `DEBUG=false`, `DEVELOPMENT=false`, `BUILD_TYPE=stats`, version `0.20.0-stats.13`, and application ID `org.jellyfin.androidtv.debug`.
- The exact requested stats command completed successfully: `./gradlew.bat :app:assembleStats "-Pjellyfin.version=0.20.0-stats.13" --console=plain`. R8 minification and resource shrinking completed. Total build time was 7 minutes 8 seconds.
- Final APK: **29,987,032 bytes** (29.99 MB decimal), up 829 bytes from the previous episode-comments APK. Includes `arm64-v8a`, `armeabi-v7a`, `x86` and `x86_64`.
- APK signature verification passed. The certificate SHA-256 fingerprint matches the previous stats APK: `5825be346f03aa4d364970042037075f8244ccfee348f39c857373cec7f192eb`.
- APK manifest inspection confirms package `org.jellyfin.androidtv.debug`, version code `200013`, version name `0.20.0-stats.13`, and absence of the debuggable application flag. The R8 mapping retains the comments API, capability serializer and dialog implementations.

Current machine-readable results are `validation/server-tests-0.2.trx`, `validation/android-discussions-tests.xml` and `validation/android-sections-tests.xml`. The stats build log, APK signature and manifest reports have a `0.2` suffix. Older reports are retained as history. API tests use fake upstream responses, not a real Trakt API credential.

## Live validation boundary

No running Jellyfin installation was changed and this updated APK was not installed on a TV by the agent. The new movie/series support and physical remote/focus behavior of the preview rows still need a live check against your server. The plugin and APK are provided as installable artifacts for that check.

The Android app makes the integration optional: capability discovery is asynchronous, fails closed, and is repeated at selection. Removal after a discussion has already opened is handled by the dialog's error state; it does not affect playback.
