cf target -s pcf-steeltoe
pause
cls

rem Delete app to update any changes to registered SSO app
cf delete pcf-ers-dotnetcore-demo -f -r
pause
cls

cf push