# RustDeskList

Утилита на .NET 10 WinForms переносит локальные списки «Последние сеансы» и «Избранное» между профилями Windows. В JSON входят ID RustDesk, псевдонимы, имена компьютеров и пользователей, ОС, отметки избранного и даты для сортировки последних сеансов.

Разработчик — **Alexandr Gedz**. Версия и ссылка на GitHub доступны по кнопке **«О программе»** или клавише **F1**.

## Перенос списка

1. На исходном компьютере запустите **RustDeskList.exe** и нажмите **Экспорт**.
2. Перенесите JSON и утилиту на другой компьютер. Запустите утилиту под той учётной записью Windows, в которой используете RustDesk.
3. Нажмите **Импорт**, выберите JSON и проверьте предварительный итог. При необходимости утилита закроет и снова откроет пользовательский клиент RustDesk. Завершите исходящие сеансы перед подтверждением.

При совпадении ID импорт заполняет пустые поля и добавляет отметки «Избранное». При подключении к ПК по обычному ID применяется сервер, настроенный в RustDesk на компьютере назначения. Серверная адресная книга RustDesk Pro синхронизируется через аккаунт RustDesk.

JSON содержит только сведения о контактах. Если для подключения на новом ПК требуется пароль, введите его в RustDesk.

Профиль находится в %APPDATA%\RustDesk\config. Копии изменяемых TOML-файлов хранятся в %LOCALAPPDATA%\RustDeskList\Backups. В файле created-files.txt перечислены файлы, созданные импортом. Для переноса списка используйте экспортированный JSON.

## Сборка

Для сборки нужен .NET 10 SDK. Для запуска опубликованного файла Windows x64 достаточно самого EXE.

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded
```

Публикация помещает RustDeskList.exe в bin\Release\net10.0-windows\win-x64\publish.

Формат локального профиля сверялся с исходным кодом RustDesk 1.5.3. [PeerConfig и LocalConfig](https://github.com/rustdesk/hbb_common/blob/fae47e024dd83cef6aaecde0aa7d11731df11649/src/config.rs)
