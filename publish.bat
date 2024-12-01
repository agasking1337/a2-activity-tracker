@echo off
echo Cleaning old files...
if exist "build" rd /s /q "build"

echo Building project...
dotnet build -c Release

echo Cleaning up unnecessary files...
cd build\counterstrikesharp\plugins\cs2-slots-tracker
del /f /q *.pdb
del /f /q *.deps.json
del /f /q System.*.dll
del /f /q Microsoft.*.dll
del /f /q *.xml

echo Keeping only necessary DLLs...
for %%i in (*.dll) do (
    if not "%%i"=="cs2-slots-tracker.dll" (
        if not "%%i"=="Dapper.dll" (
            if not "%%i"=="MySqlConnector.dll" (
                if not "%%i"=="System.Memory.dll" (
                    if not "%%i"=="System.Runtime.CompilerServices.Unsafe.dll" (
                        del "%%i"
                    )
                )
            )
        )
    )
)

echo Done! Files ready in build/counterstrikesharp/plugins/cs2-slots-tracker
cd ..\..\..\..
