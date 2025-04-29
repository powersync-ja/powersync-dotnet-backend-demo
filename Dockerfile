# Use official .NET 9 SDK image as the build image
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Set the working directory inside the container
WORKDIR /app

# Copy only the .csproj file first to leverage Docker caching
COPY ./PowerSync.Api/PowerSync.Api.csproj ./PowerSync.Api/

# Restore dependencies
WORKDIR /app/PowerSync.Api
RUN dotnet restore

# Copy the entire project and build the application
COPY . /app
RUN dotnet publish -c Release -o /out

# Use the .NET 9 runtime image for the final container
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# Set environment variables
ENV DATABASE_URI=
ENV DATABASE_TYPE=
ENV POWERSYNC_PRIVATE_KEY=
ENV POWERSYNC_PUBLIC_KEY=
ENV POWERSYNC_URL=
ENV PORT=5000
ENV JWT_ISSUER=

# Set the working directory
WORKDIR /app

# Copy the built application from the build image
COPY --from=build /out ./

# Expose the port
EXPOSE 5000

# Run the application
CMD ["dotnet", "PowerSync.Api.dll"]
