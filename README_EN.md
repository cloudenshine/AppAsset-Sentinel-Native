# AppAsset Sentinel Native

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?logo=windows&logoColor=white" alt="Windows" />
  <img src="https://img.shields.io/badge/.NET-9.0%20Native-512BD4?logo=dotnet&logoColor=white" alt=".NET 9" />
  <img src="https://img.shields.io/badge/UI-Photino%20WebView2-blue" alt="Photino" />
  <img src="https://img.shields.io/badge/Tests-102%20Passed-emerald" alt="Tests" />
  <img src="https://img.shields.io/badge/License-Apache%202.0-orange.svg" alt="License" />
</p>

> **The Intelligent Windows Software Asset Lifecycle Sentinel for Developers and the Local AI Era.**  
> Built with **C# (.NET 9) + Win32 Native P/Invoke + Photino (Edge WebView2)**. Eliminates catastrophic environmental breakages, disk bloat, and AI model deletion through storage tiering, dual-lock Junction relocation, and DAG dependency shielding.

> **⚠️ Current release: R0 safe-observation (read-only)**
>
> This build exposes **observation, display and plan preview only**. Relocation,
> auto-heal, force-clean, live uninstall, junction unlink and restore-point creation
> are **not enabled**; actions are not offered in the UI and the API returns an
> explicit `Unsupported` / `Blocked` result with a reason — never a success.
>
> Check the live posture at `http://127.0.0.1:8765/api/policy`.
> See [`appasset-audit/RESPONSE.md`](appasset-audit/RESPONSE.md) for the remediation record.
>
> Sections below describe **intended** capability and design, not what ships today.

---

## 🌟 Why AppAsset Sentinel Native?

Traditional uninstallers (born in the legacy PC era) fail critically when dealing with **modern developer stacks and local AI workstations**:
* **Runtime Fragility**: Deleting Python, CUDA Toolkit, or Git breaks dozens of downstream AI pipelines (ComfyUI, Ollama, Docker).
* **Massive Model Asset Deletion**: Deleting an AI tool often destroys 40GB+ of hard-won GGUF/Safetensors weights stored in default folders.
* **Storage Drift & C-Drive Saturation**: AI models and Docker VHDX disks quickly choke drive C:. Simple file moves break paths, while future downloads drift straight back to the system drive.

**AppAsset Sentinel Native** solves this through 4 engineered pillars:

1. **📋 Multi-Source Inventory & Objective Telemetry**: 6-tier evidence harvesting across processes, services, shortcuts, AppData files, and executable timestamps.
2. **🚀 Multi-Volume Asset Vault & Dual-Lock Anti-Drift**: Detects **NVMe SSD vs SATA HDD** hardware media types. Relocates heavy assets via Windows kernel-level **NTFS Junctions** with environment variable synchronization, supported by an auto-healing **Drift Watchdog**.
3. **🛡️ DAG Dependency Shield & Decoupled Rules**: Static DLL/binary snooping without human intervention, combined with 100% externalized JSON rules that can be hot-reloaded dynamically.
4. **🧯 Safety Fuse & Snapshot Vault**: Hardcoded critical directory protection (`C:\Windows`, `System32`), System Restore Point triggers, and zero-permission instant registry `.reg` backup vaults.

---

## 🛠️ Quick Start

### 1. Download Standalone Executable
Grab the latest single-file `.exe` from the [Releases page](https://github.com/cloudenshine/AppAsset-Sentinel-Native/releases/tag/v1.0.0). No Python or .NET runtime installation required.

### 2. Build from Source
```powershell
git clone https://github.com/cloudenshine/AppAsset-Sentinel-Native.git
cd AppAsset-Sentinel-Native

# Run automated tests (28 passed)
dotnet test src/AppAssetSentinel.Tests/AppAssetSentinel.Tests.csproj

# Run native app in dev mode
dotnet run --project src/AppAssetSentinel.App/AppAssetSentinel.App.csproj

# Publish self-contained single-file binary
dotnet publish src/AppAssetSentinel.App/AppAssetSentinel.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./dist
```

---

## 📄 License

Licensed under the [Apache License 2.0](LICENSE).
