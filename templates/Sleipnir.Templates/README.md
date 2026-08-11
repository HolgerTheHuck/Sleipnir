# Sleipnir.Templates

dotnet new templates for Sleipnir.

## Install

```bash
dotnet new install Sleipnir.Templates
```

## Templates

| Name                 | Short name         | Description                               |
| -------------------- | ------------------ | ----------------------------------------- |
| Sleipnir Server         | `sleipnir-server`     | Minimal ASP.NET Core + Sleipnir server       |
| Sleipnir Server + SPA   | `sleipnir-server-spa` | Sleipnir server with a Svelte 5 front end    |

## Usage

```bash
# Minimal server
dotnet new sleipnir-server -n HelloSleipnir
cd HelloSleipnir
dotnet run --launch-profile https

# Server + Svelte SPA
dotnet new sleipnir-server-spa -n HelloSleipnirSpa
cd HelloSleipnirSpa
cd web && npm install && npm run dev
cd ..
dotnet run --launch-profile https
```
