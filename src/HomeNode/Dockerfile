# Tellhouse deployment skeleton: the app as a container.
# Build from this directory:  docker build -t tellhouse .
# Run:                        docker run -p 8080:8080 tellhouse
# The seeded demo household is created on first start (EnsureCreated +
# DbSeeder); the SQLite file lives inside the container, so a restart
# reseeds the same fictional day. No real person's data is involved.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY HomeNode.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HomeNode.dll"]
