# Практическая работа — вариант 12
## CampusDbSync: миграция локальной SQLite БД АРМ

Утилита C# / .NET 8 мигрирует таблицу `Passes` со схемы v1 на v2, создает резервную копию, проверяет результат, журналирует действия и формирует Markdown-отчет.

### Запуск
Требуется .NET 8 SDK.

```powershell
cd src/CampusDbSync
dotnet restore
dotnet build -c Release
dotnet run -- --demo
```

Для существующей БД:
```powershell
dotnet run -- --db "C:\ProgramData\Campus\campus.db"
```

Демо создаёт тестовую БД v1 с тремя записями, резервирует её и выполняет миграцию до v2. API `https://jsonplaceholder.typicode.com/users` используется как независимая проверка сетевой доступности. Недоступность API не блокирует локальную миграцию.

### Модель данных
- v1: `Passes(Id, PersonId, PassDate)`.
- v2: `Passes(Id, PersonId, PassDate, AccessPoint, Status)`.
- Версия хранится в `PRAGMA user_version`.
- Для старых записей новые поля получают `UNKNOWN` и `Active`.

Перед использованием на реальном АРМ необходимо сверить имена таблиц/полей с фактической БД и остановить процессы, выполняющие запись.
