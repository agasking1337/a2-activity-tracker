@echo off
mkdir deploy
xcopy /Y /S bin\Debug\net7.0\*.* deploy\
echo Files have been copied to the deploy folder. Copy these files to your CS2 server's plugins directory.
