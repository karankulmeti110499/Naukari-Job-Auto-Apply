FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["JobAutoApplyMvc/JobAutoApply.Web/JobAutoApply.Web.csproj", "JobAutoApply.Web/"]
RUN dotnet restore "JobAutoApply.Web/JobAutoApply.Web.csproj"

COPY JobAutoApplyMvc/. .
WORKDIR "/src/JobAutoApply.Web"
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "JobAutoApply.Web.dll"]