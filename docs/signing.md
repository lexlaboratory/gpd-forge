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
2. **Signed native app:** a Tauri `.exe` + installer, code-signed so SAC trusts it. Which certificate
   to use depends on **where the signer is legally located** — see the next section, which is the
   thing to read before anything else.

## 🔴 Check eligibility BEFORE choosing a route

The obvious answer is Azure (now called **Artifact Signing**, formerly Trusted Signing), and this
document used to recommend it without qualification. That was wrong for this project, and checking
took ten minutes:

> Public Trust certificates are available to **organizations** in the United States, Canada, the EU,
> the UK, Australia, New Zealand, Japan, South Korea, Singapore, Switzerland, Norway, and Israel.
> **Individual developers must be located in the United States or Canada.**
> — [Artifact Signing quickstart](https://learn.microsoft.com/en-us/azure/trusted-signing/quickstart)

**This project's maintainer is in Mexico, which is on neither list.** Azure is therefore not
available to us at all, and no amount of Azure tooling changes that: identity validation is sourced
from the Azure **billing account**, and the account type must match the validation type.

Worth being explicit about a related dead end, because it is an intuitive guess: **having Azure
software installed on a server does nothing for this.** Signing happens in GitHub Actions through a
service principal, against keys held in Microsoft's HSM. There is no local certificate, no hardware
token, and nothing to install anywhere. What matters is a subscription plus an eligible legal
identity — not tooling.

### Route A — SignPath Foundation (free, no geographic restriction) ← recommended for GPD Forge

[SignPath Foundation](https://signpath.org/terms) sponsors code signing for open-source projects.
The certificate is issued **to the Foundation as the legal entity**, and qualifying projects sign
under it — which is exactly why the maintainer's country does not matter.

Their conditions, and where GPD Forge stands against each:

| Condition | GPD Forge |
| --- | --- |
| No malware or PUP | ✔ |
| OSI-approved licence, **no commercial dual-licensing** | ✔ GPL-3, and "a commercial/closed edition" is an explicit non-goal in `ROADMAP.md` |
| No proprietary components | ✔ (PresentMon is Intel's, redistributed, signed by Intel; not ours and not proprietary to us) |
| Actively maintained | ✔ |
| Already released | ✔ v0.3.0 |
| Documented functionality | ✔ README + `docs/` |

**The trade-off, stated plainly:** binaries would be signed as *SignPath Foundation*, not as
*lexlaboratory*. If the publisher name shown to users matters, this is the wrong route.

Their certificates are **OV**. On SAC that normally means a reputation ramp — but the Foundation's
certificate signs many projects, so it carries accumulated reputation a brand-new OV cert would not.
⚠️ That is a reasonable expectation, **not a measured fact**: verify on a SAC machine before
promising it to anyone.

### Route B — Buy a certificate (paid, your own name)

Commercial CAs sell to Mexico and offer cloud signing that works in CI without a hardware token:
[SSL.com](https://www.ssl.com/products/software-integrity/code-signing/) (eSigner),
[Sectigo](https://www.sectigo.com/ssl-certificates-tls/code-signing),
[Certum](https://www.certum.eu/en/code-signing-certificates/). Choose **EV** if SAC/SmartScreen trust
needs to be immediate; OV is cheaper and ramps.

### Route C — Stay on Via 0

The current state, and not a failure: the portable bundle runs under SAC today with no certificate at
all. The only thing missing is a native installer, and `install-gpd-forge.ps1` builds one locally
when needed.

The `release` workflow always builds the Via-0 portable bundle. The signed native job is **gated** and
skipped until you activate signing below.

## Activate Azure Trusted Signing (only if the eligibility check above passes)

⚠️ Kept for completeness and for anyone forking this project from an eligible country. **It does not
apply to this repository's maintainer** — see the eligibility section above.
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
