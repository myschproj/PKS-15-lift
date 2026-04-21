Для работы кода в проекте должен быть установлен NuGet-пакет Newtonsoft.Json и OxyPlot.Wpf

Установить их можно через Tools → NuGet Package Manager → Manage NuGet Packages for Solution.

Если при компиляции возникнет ошибка типа The type or namespace name 'OxyPlot' could not be found, нужно проверить, что пакет OxyPlot.Wpf установлен и проект собран для целевой платформы net8.0-windows (или аналогичной, но лучше именно 8 дотнет под wf).


## Инструкция по запуску

1. Создай проект в Visual Studio:
   · Выбери шаблон WPF Application для .NET 8.0 (или .NET Core 3.1+).
2. Поставь NuGet-пакеты (через Manage NuGet Packages):
   - Newtonsoft.Json (версия 13.0.3 или выше)
   - ОxyPlot.Wpf (версия 2.1.0 или выше)
4. Скопируй код из xaml файла в MainWindow.xaml, а C# код в MainWindow.xaml.cs.
5. Настрой права для HTTP-сервера (если по http http://+:{port}/), и потом запусти Visual Studio от имени администратора или выполни в командной строке (от имени админа) вот этот код:
     ```
     netsh http add urlacl url=http://+:8080/ user=Everyone
     ```
     Альтернативно измени в коде префикс на http://localhost:8080/ (не требует прав).
7. Запусти приложение (можно на Start, можно на F5).

Важно, чтобы пакеты написанные выше были установлены.
