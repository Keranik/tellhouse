# Tellhouse home node: the node as a service, with its backup tool.
#
# Build context change: build from ./src, not from the HomeNode
# directory. From the repository root that is:
#   docker build -f src/HomeNode/Dockerfile -t tellhouse src
# The context changed because the image carries the product's own
# backup tool (TellhouseBackup) alongside the node, so a fresh
# boot can restore the sealed demo snapshot by the product's own
# machinery (TellhouseBackup demo-reset, run by entrypoint.sh)
# instead of by any second seeding path invented for deployment.
#
# Image layout:
#   /app                     HomeNode publish
#   /app/backup              TellhouseBackup publish
#   /app/demo-seed/          sealed demo snapshot, demo recovery
#                            phrase, and demo node key (see
#                            HomeNode/demo-seed/README.md; all of
#                            it is demo material for the fictional
#                            Ellis household)
#   /app/entrypoint.sh       prepares the keystore, restores the
#                            demo snapshot on a fresh disk only,
#                            then starts the node
#
# No real person's data is involved.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY HomeNode/HomeNode.csproj HomeNode/
COPY TellhouseBackup/TellhouseBackup.csproj TellhouseBackup/
RUN dotnet restore HomeNode/HomeNode.csproj
RUN dotnet restore TellhouseBackup/TellhouseBackup.csproj
COPY HomeNode/ HomeNode/
COPY TellhouseBackup/ TellhouseBackup/
RUN dotnet publish HomeNode/HomeNode.csproj -c Release -o /app --no-restore
RUN dotnet publish TellhouseBackup/TellhouseBackup.csproj -c Release -o /app/backup --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./
COPY --from=build /app/backup ./backup/
COPY HomeNode/demo-seed/ ./demo-seed/
COPY HomeNode/entrypoint.sh ./entrypoint.sh
RUN chmod +x /app/entrypoint.sh
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["/app/entrypoint.sh"]
