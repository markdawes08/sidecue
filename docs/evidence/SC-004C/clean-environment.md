# Clean Windows storage verification

This is the remaining SC-004C procedure, not a claim that the run occurred. Use a disposable clean Windows x64 VM or machine with a current-user profile, no real meeting data and no existing Sidecue user data. Record the OS build, account privilege level and exact repository revision. Do not treat an ordinary developer profile as a clean environment.

1. Provide the reviewed SQLCipher DLL through a trusted transfer to a local directory protected from untrusted modification. Check SHA-256 against [the SC-004A manifest](../SC-004A/candidate-manifest.json). A different binary requires its own provenance review and validation; do not alter the pinned loader merely to make a test pass. No application package exists yet.
2. Provide the source checkout and the SDK pinned in `global.json`. If needed, `powershell -NoProfile -File tools/setup-sdk.ps1` installs the hash-verified SDK under the checkout's ignored `artifacts/`; package restore needs the configured NuGet source. Keep the native DLL outside folders writable by untrusted identities.
3. From the repository root, use the verified absolute DLL path in these commands:

   ```powershell
   powershell -NoProfile -File tools/probe-sqlcipher.ps1 -NativePath '<absolute-reviewed-dll-path>'
   powershell -NoProfile -File tools/probe-storage.ps1 -NativePath '<absolute-reviewed-dll-path>'
   powershell -NoProfile -File tools/probe-durability.ps1 -NativePath '<absolute-reviewed-dll-path>'
   ```

4. Record exact commands, exit codes, SQLCipher/SQLite/OpenSSL versions, DLL and managed-binary hashes, test-source revision, actual directory permissions and all failures in a sanitized evidence record using [the template](../../../templates/TEST_EVIDENCE.md). The probes use generated synthetic data in ignored `artifacts/`; do not attach protected keys or databases. The durability probe forcibly terminates only its own child processes.
5. Verify the tested loaded path and identity, wrong/no-key rejection, FTS and encrypted artifact scans, same-profile restart, and each reported interruption boundary. A successful local run does not prove clean-environment or packaged-binary behavior. Record the current-user DPAPI portability limitation; do not claim cross-profile recovery without a separate test.

The native rebuild recipe remains disabled until a patched Windows Perl toolchain is selected and verified. SC-006A/B separately track notices, installed-directory protection, and startup ownership for the eventual development package. Do not mark SC-004C or its parent done until all of the leaf's acceptance and remaining backlog actions have evidence.
