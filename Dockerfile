# Multi-stage Dockerfile for .NET 10 ASP.NET app
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# copy csproj and restore
COPY ["FingerjetHelper/FingerjetHelper.csproj", "FingerjetHelper/"]
RUN dotnet restore "FingerjetHelper/FingerjetHelper.csproj"

# copy rest and publish
COPY . .
WORKDIR /src/FingerjetHelper
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS="http://+:80"
EXPOSE 80
ENTRYPOINT ["dotnet", "FingerjetHelper.dll"]
