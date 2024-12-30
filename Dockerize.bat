@echo off

docker build -t mrgaton/dsfiles_server -f Dockerfile .
docker tag dsfiles_server mrgaton/dsfiles_server
docker push mrgaton/dsfiles_server

timeout /t 3