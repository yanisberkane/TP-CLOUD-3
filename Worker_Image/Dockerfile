# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base
USER $APP_UID
WORKDIR /app

# Pour le managed identity
ENV AZURE_CLIENT_ID="b3a6ebd9-f34e-4b23-9ca8-bd40056c383d"
ENV AZURE_CLIENT_SECRET="o8f8Q~m2YQq~l4tQnpFBOL3574Bt39f~uWGK6duN"
ENV AZURE_TENANT_ID="901cb4ca-b862-4029-9306-e5cd0f6d9f86"

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Worker_Image/Worker_Image.csproj", "Worker_Image/"]
RUN dotnet restore "./Worker_Image/Worker_Image.csproj"
COPY . .
WORKDIR "/src/Worker_Image"
RUN dotnet build "Worker_Image.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Worker_Image.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "Worker_Image.dll"]