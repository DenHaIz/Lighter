<p align="center"><img src="docs/logo.png" width="96" alt="Lighter"></p>

<h1 align="center">Lighter</h1>

Windows startup manager in one small `.exe`.

**[Download](../../releases/latest)** · Windows 10/11, Server 2016+ · no installation

- See everything that starts with Windows: registry, Startup folder, Task Scheduler, services — and places Task Manager does not show (Winlogon, IFEO, Active Setup, BootExecute, AppInit_DLLs)
- For every user of the PC, not only the one running Lighter; filter by user
- Digital signatures are checked: unsigned files stand out, Windows components can be hidden
- Turn entries off and on — just like Task Manager
- Delete safely — a backup copy is saved first
- View running processes and end them
- Dark and light theme, English and Russian

Build from source: run `build.cmd` (no SDK needed). Developer notes: [HANDOFF.md](HANDOFF.md) (in Russian).

---

Менеджер автозагрузки Windows в одном небольшом `.exe`.

**[Скачать](../../releases/latest)** · Windows 10/11, Server 2016+ · без установки

- Показывает всё, что запускается вместе с Windows: реестр, папку «Автозагрузка», планировщик, службы — и места, которых нет в Диспетчере задач (Winlogon, IFEO, Active Setup, BootExecute, AppInit_DLLs)
- У всех пользователей ПК, а не только у того, кто запустил программу; фильтр по пользователю
- Проверяет цифровую подпись: файлы без подписи выделены, компоненты Windows можно скрыть
- Отключайте и включайте записи — так же, как в Диспетчере задач
- Безопасное удаление — сначала сохраняется резервная копия
- Список запущенных процессов с возможностью их завершить
- Тёмная и светлая тема, русский и английский язык

Сборка из исходников: запустите `build.cmd` (SDK не нужен). Заметки для разработчиков: [HANDOFF.md](HANDOFF.md).

---

[MIT License](LICENSE)
