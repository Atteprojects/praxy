# ---- console build ----
FROM node:24-alpine AS console-build
WORKDIR /console
COPY console/package.json console/package-lock.json ./
RUN npm ci
COPY console/ ./
RUN npm run build

# ---- api build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY Praxy.sln Directory.Build.props ./
COPY src/ src/
RUN dotnet publish src/Praxy.Api -c Release -o /app/publish

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# Sites Phase 4: a git-sourced deployment is cloned by shelling out to the system `git` CLI
# (Praxy.Vcs.GitCliRepositoryCloner) rather than a library — this base image doesn't carry it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*
COPY --from=api-build /app/publish ./
# The console is static assets served by the API at the root — one container, no CORS.
COPY --from=console-build /console/dist ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Praxy.Api.dll"]
