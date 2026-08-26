# Code signing & Smart App Control

## The situation
Windows 11 **Smart App Control (SAC)**, in Enforcement, blocks executables it can't establish trust
for through Microsoft's Intelligent Security Graph (ISG). A valid signature alone isn't always enough
— the signer needs *reputation*. Consequences:

- **Self-signed** certs (even added to Trusted Root) do **not** satisfy SAC — it doesn't consult your
  local root store.
- Turning SAC **off is irreversible** (you must reset Windows to turn it back on). Don't.
- **OV** certs work but reputation accrues over time; early downloads may still warn.
- **EV** certs and **Azure Trusted Signing** get ISG trust quickly.

## Two supported models
1. **Via 0 (default, no certificate):** never launch an unsigned binary of ours. The service is hosted
   by the signed `dotnet.exe`; the UI runs in the (signed) browser; scripts run under signed
   `powershell.exe`. This is what `install-gpd-forge.ps1` and the **portable** bundle do — they run
   under SAC today with no cert. Ship this now.
2. **Signed native app:** a Tauri `.exe` + installer, code-signed so SAC trusts it. Use **Azure
   Trusted Signing** (~US$10/mo, Microsoft-operated, cloud key — no hardware token, ideal for CI).

The `release` workflow always builds the Via-0 portable bundle. The signed native job is **gated** and
skipped until you activate signing below.

## Activate Azure Trusted Signing
1. In Azure, create a **Trusted Signing** account + a **certificate profile** (Public Trust). Complete
   the identity validation (individual or organization).
2. Create an **app registration** (service principal) and grant it the **Trusted Signing Certificate
   Profile Signer** role on the account.
3. Add these **repository secrets** (Settings → Secrets and variables → Actions):
   - `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`
   - `TRUSTED_SIGNING_ENDPOINT` (e.g. `https://wus2.codesigning.azure.net/`)
   - `TRUSTED_SIGNING_ACCOUNT` (account name)
   - `TRUSTED_SIGNING_PROFILE` (certificate profile name)
4. Add the **repository variable** `SIGN_RELEASE = true`.
5. Push a `vX.Y.Z` tag. The `signed-native` job builds the Tauri app + installer and signs them with
   `azure/trusted-signing-action`, then attaches them to the Release.

### Verify before the first signed release
- Confirm the Tauri build command in `.github/workflows/release.yml` matches this repo
  (`npm --prefix ui run tauri -- build`) and that the bundle lands under
  `ui/src-tauri/target/release/bundle/{nsis,msi}/`.
- After the run, on a SAC machine: download the signed installer and confirm it launches without a
  SAC/SmartScreen block (EV/Trusted Signing should be immediate; brand-new profiles may take a short
  reputation ramp).

## Cheap stopgap (no cert)
Submit the built binaries to Microsoft for analysis (SmartScreen/SAC "submit for analysis") to seed
reputation. It helps SmartScreen more than SAC and isn't guaranteed, but it's free.
