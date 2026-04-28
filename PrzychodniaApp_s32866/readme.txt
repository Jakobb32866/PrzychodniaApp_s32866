Serwer bazy danych uruchamiany lokalnie na dockerze:

(Trzeba najpierw zainstalować dockera)

docker run -e "ACCEPT_EULA=Y" -e "SA_PASSWORD=Password@12345" \
  -p 1433:1433 --name sqlserver \              
  -d mcr.microsoft.com/mssql/server:2022-latest
 
//Znajdź plik tworzący rbd.
find ~ -name "01_create_and_seed_clinic.sql"

docker exec -i sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "Password@12345" \
  -C -i /dev/stdin < ((Ścieżka z poprzedniej komendy))