REM publish
mkdir -p ../../build
dotnet publish -c Release --framework netcoreapp2.0 -o ../../build
