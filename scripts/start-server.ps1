Start-Process -FilePath "C:\Program Files\nodejs\npx.cmd" -ArgumentList "http-server","C:\Users\Ian\Code\Claude\here-workspace\public","-p","8080","-c-1" -WindowStyle Hidden
Write-Output "HTTP server started"
