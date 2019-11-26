@ECHO off

:: Publish SER to the folders 
:: Tools: dotnet, 7zip
:: Path to QNAS required

:: Set main folder
SET batFolder=%~dp0
SET mainFolder=%batFolder%..\..\..\..\TestArea
echo Mainfolder: %mainFolder%

:: Call dotnet publish to the installer folder
dotnet publish --configuration Release --framework netcoreapp3.0 --self-contained --runtime win-x64 --output "%mainFolder%\Installer\Sense Excel Reporting\Connector"

:: Create Version with connector and Read version number from Version.txt
"%mainFolder%\Installer\Sense Excel Reporting\Connector\SerConAai.exe" VersionNumber
SET /p buildnum=<"%mainFolder%\Installer\Sense Excel Reporting\Connector\Version.txt"

:: Create installer zip file
"C:\Program Files\7-Zip\7z.exe" a -tzip "%mainFolder%\SER-%buildnum%.zip" "%mainFolder%\Installer\*"

:: Kopieren der Zip auf Transfer for product manager
xcopy /y "%mainFolder%\SER-%buildnum%.zip" \\qnas\transfer\Martin\Reporting

pause