# Trame.Templates

dotnet new templates for Trame.

## Install

```bash
dotnet new install Trame.Templates
```

## Templates

| Name                 | Short name         | Description                               |
| -------------------- | ------------------ | ----------------------------------------- |
| Trame Server         | `trame-server`     | Minimal ASP.NET Core + Trame server       |
| Trame Server + SPA   | `trame-server-spa` | Trame server with a Svelte 5 front end    |

## Usage

```bash
# Minimal server
dotnet new trame-server -n HelloTrame
cd HelloTrame
dotnet run --launch-profile https

# Server + Svelte SPA
dotnet new trame-server-spa -n HelloTrameSpa
cd HelloTrameSpa
cd web && npm install && npm run dev
cd ..
dotnet run --launch-profile https
```
