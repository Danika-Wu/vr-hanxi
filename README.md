# VR Hanxi — Unity / Meta Quest project

This repository contains the Unity project from `D:\VR\My project`.

## Clone and open

1. Install Git and Git LFS.
2. Clone the repository.
3. Run `git lfs install` once.
4. Open the cloned folder with the Unity version recorded in `ProjectSettings/ProjectVersion.txt`.
5. Let Unity restore packages and rebuild `Library/` locally.

Do not copy or commit `Library`, `Temp`, `Logs`, `UserSettings`, or generated IDE files.

## Team workflow

- Save scenes and assets before expecting them to sync.
- The scheduled helper checks every five minutes, commits stable saved changes, fetches the remote, and pushes automatically.
- It does not pull teammate changes into an open Unity project. Close Unity to allow the next scheduled run to pull safely.
- To run a check immediately, double-click `Tools/Sync-Now.cmd`.
- If `.github-sync/sync.log` reports a conflict, stop editing the conflicted scene or prefab and resolve it together before continuing.
- A teammate can enable the same helper by running:

```powershell
powershell -ExecutionPolicy Bypass -File .\Tools\Install-GitHubAutoSync.ps1
```

## Large model policy

- The optimized textured bedroom model is tracked with Git LFS.
- The original pre-decimation `Assets/Model/Model1/bedroom/mybedroom.obj` (about 4.44 GB) and its old MTL are intentionally excluded.
- The textures in that original folder are still tracked because the optimized materials reference them.

