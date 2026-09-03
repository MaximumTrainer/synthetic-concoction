# ---------- build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Fabricate.slnx ./
COPY Directory.Build.props ./
COPY global.json ./
COPY Fabricate.Domain/Fabricate.Domain.csproj Fabricate.Domain/
COPY Fabricate.Application/Fabricate.Application.csproj Fabricate.Application/
COPY Fabricate.Infrastructure/Fabricate.Infrastructure.csproj Fabricate.Infrastructure/
COPY Fabricate.Api/Fabricate.Api.csproj Fabricate.Api/

RUN dotnet restore Fabricate.Api/Fabricate.Api.csproj

COPY Fabricate.Domain/ Fabricate.Domain/
COPY Fabricate.Application/ Fabricate.Application/
COPY Fabricate.Infrastructure/ Fabricate.Infrastructure/
COPY Fabricate.Api/ Fabricate.Api/

RUN dotnet publish Fabricate.Api/Fabricate.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ---------- runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Fabricate.Api.dll"]
