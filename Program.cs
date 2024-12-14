using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace ExaHelperCS {
    class Program {
        private static ITelegramBotClient? _botClient;
        private const string ConnectionString = "Host=localhost;Username=postgres;Password=admin;Database=telegram_bot_db";
        private static Dictionary<long, (string subject, int questionIndex, int score)> _userQuizStates = new Dictionary<long, (string, int, int)>();
        private static HashSet<long> _usersWelcomed = new HashSet<long>();

        static async Task Main() {
            _botClient = new TelegramBotClient("TOKEN_HERE");
            var me = await _botClient.GetMeAsync();
            Console.WriteLine($"Бот запущен: {me.FirstName}");
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            await ReceiveUpdates(cancellationToken);
        }

        private static async Task ReceiveUpdates(CancellationToken cancellationToken) {
            int offset = 0;
            while (!cancellationToken.IsCancellationRequested) {
                try {
                    if (_botClient != null) {
                        var updates = await _botClient.GetUpdatesAsync(offset, cancellationToken: cancellationToken);
                        foreach (var update in updates) {
                            offset = update.Id + 1; // Обновляем offset
                            if (update.Message != null && update.Type == UpdateType.Message && update.Message.Text != null) {
                                await HandleMessage(update.Message);
                            } else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null) {
                                await HandleCallbackQuery(update.CallbackQuery);
                            }
                        }
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"Ошибка в получении обновлений: {ex.Message}");
                }
                await Task.Delay(1000); // Задержка перед следующим запросом
            }
        }

        private static async Task HandleMessage(Message message) {
            Debug.Assert(message.Text != null, "message.Text != null");
            long chatId = message.Chat.Id;
            if (message.Text.ToLower() == "/start") {
                if (!_usersWelcomed.Contains(chatId)) {
                    await _botClient.SendTextMessageAsync(chatId, "Добро пожаловать! Выберите предмет для викторины:", replyMarkup: GetSubjectMenu());
                    _usersWelcomed.Add(chatId); // Добавляем пользователя в список
                }
            } else {
                await _botClient.SendTextMessageAsync(chatId, "Неизвестная команда. Пожалуйста, используйте /start для начала.");
            }
        }

        private static IReplyMarkup GetSubjectMenu() {
            return new InlineKeyboardMarkup(new[] {
                new[] { InlineKeyboardButton.WithCallbackData("Математика"), InlineKeyboardButton.WithCallbackData("Физика") },
                new[] { InlineKeyboardButton.WithCallbackData("Химия"), InlineKeyboardButton.WithCallbackData("История") },
                new[] { InlineKeyboardButton.WithCallbackData("Посмотреть результаты") }
            });
        }

        private static async Task StartQuiz(long chatId, string quizSubject) {
            Console.WriteLine($"Пользователь {chatId} начал викторину по предмету: {quizSubject}");
            _userQuizStates[chatId] = (quizSubject, 0, 0); // Состояние викторины для пользователя
            await SendQuizQuestion(chatId);
        }

        private static async Task SendQuizQuestion(long chatId) {
            if (!_userQuizStates.ContainsKey(chatId)) return;

            var (currentSubject, questionIndex, score) = _userQuizStates[chatId];
            using (var conn = new NpgsqlConnection(ConnectionString)) {
                conn.Open();
                var cmd = new NpgsqlCommand("SELECT * FROM questions WHERE subject = @subject LIMIT 1 OFFSET @offset", conn);
                cmd.Parameters.AddWithValue("subject", currentSubject);
                cmd.Parameters.AddWithValue("offset", questionIndex);

                using (var reader = cmd.ExecuteReader()) {
                    if (reader.Read()) {
                        string question = reader.GetString(2);
                        string correctAnswer = reader.GetString(3);
                        string wrongAnswer1 = reader.GetString(4);
                        string wrongAnswer2 = reader.GetString(5);
                        string wrongAnswer3 = reader.GetString(6);
                        var answers = new List<string> { correctAnswer, wrongAnswer1, wrongAnswer2, wrongAnswer3 };
                        var randomAnswers = answers.OrderBy(a => Guid.NewGuid()).ToList();

                        // Увеличиваем индекс вопроса
                        _userQuizStates[chatId] = (currentSubject, questionIndex + 1, score);
                        var inlineKeyboard = new InlineKeyboardMarkup(randomAnswers.Select(answer => InlineKeyboardButton.WithCallbackData(answer)).ToArray());

                        // Отправляем вопрос пользователю
                        await _botClient.SendTextMessageAsync(chatId, question, replyMarkup: inlineKeyboard);
                    } else {
                        // Викторина завершена
                        Console.WriteLine($"Викторина завершена для пользователя {chatId}. Результат: {score} баллов.");
                        await SaveUserScore(chatId, currentSubject, score);
                        await _botClient.SendTextMessageAsync(chatId, $"Викторина окончена! Ваш результат: {score} баллов.\nВыберите действие:", replyMarkup: GetPostQuizMenu());
                        _userQuizStates.Remove(chatId); // Удаляем состояние
                    }
                }
            }
        }

        private static IReplyMarkup GetPostQuizMenu() {
            return new InlineKeyboardMarkup(new[] {
                new[] { InlineKeyboardButton.WithCallbackData("Вернуться в меню выбора предметов"), InlineKeyboardButton.WithCallbackData("Перепройти тест") },
                new[] { InlineKeyboardButton.WithCallbackData("Посмотреть результаты") }
            });
        }

        private static async Task HandleCallbackQuery(CallbackQuery callbackQuery) {
            long chatId = callbackQuery.Message.Chat.Id;

            // Подтверждение нажатия кнопки
            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id);

            if (callbackQuery.Data == "Посмотреть результаты") {
                await ShowUserScores(chatId);
                return;
            }

            if (callbackQuery.Data == "Математика" || callbackQuery.Data == "Физика" || callbackQuery.Data == "Химия" || callbackQuery.Data == "История") {
                await StartQuiz(chatId, callbackQuery.Data); // Запускаем викторину по выбранному предмету
                return;
            }

            if (_userQuizStates.TryGetValue(chatId, out var quizState)) {
                var (subjectName, questionIndex, score) = quizState;

                using (var conn = new NpgsqlConnection(ConnectionString)) {
                    conn.Open();
                    var cmd = new NpgsqlCommand("SELECT correct_answer FROM questions WHERE subject = @subject LIMIT 1 OFFSET @offset", conn);
                    cmd.Parameters.AddWithValue("subject", subjectName);
                    cmd.Parameters.AddWithValue("offset", questionIndex - 1); // Получаем правильный ответ предыдущего вопроса

                    string correctAnswer;
                    try {
                        correctAnswer = (string)await cmd.ExecuteScalarAsync();
                    } catch (Exception ex) {
                        Console.WriteLine($"Ошибка при получении правильного ответа для пользователя {chatId}: {ex.Message}");
                        return;
                    }

                    if (callbackQuery.Data == correctAnswer) {
                        score++;
                        await _botClient.SendTextMessageAsync(chatId, "Правильно! 🎉");
                    } else {
                        await _botClient.SendTextMessageAsync(chatId, $"Неправильно. Правильный ответ: {correctAnswer}.");
                    }

                    // Обновляем состояние с новым счётом
                    _userQuizStates[chatId] = (subjectName, questionIndex, score);
                    await SendQuizQuestion(chatId); // Отправляем следующий вопрос
                }

                if (callbackQuery.Data == "Вернуться в меню выбора предметов") {
                    await _botClient.SendTextMessageAsync(chatId, "Выберите предмет для викторины:", replyMarkup: GetSubjectMenu());
                    _userQuizStates.Remove(chatId); // Удаляем состояние
                    return;
                }

                if (callbackQuery.Data == "Перепройти тест") {
                    await StartQuiz(chatId, quizState.subject); // Перезапускаем викторину с тем же предметом
                    return;
                }
            } else {
                Console.WriteLine($"Состояние викторины не найдено для пользователя {chatId}. Возможно тест не был начат.");
            }
        }

        private static async Task SaveUserScore(long chatId , string subjectName , int score) {
            using (var conn = new NpgsqlConnection(ConnectionString)) {
                conn.Open();
                var cmd = new NpgsqlCommand("INSERT INTO user_scores (user_id , subject , score , created_at) VALUES (@user_id , @subject , @score , CURRENT_TIMESTAMP)", conn);
                cmd.Parameters.AddWithValue("user_id", chatId);
                cmd.Parameters.AddWithValue("subject", subjectName );
                cmd.Parameters.AddWithValue("score", score);

                try {
                    await cmd.ExecuteNonQueryAsync();
                } catch (Exception ex) {
                    Console.WriteLine($"Ошибка при сохранении результата пользователя {chatId}: {ex.Message}");
                }
            }
        }

        private static async Task ShowUserScores(long chatId) {
            using (var conn = new NpgsqlConnection(ConnectionString)) {
                conn.Open();
                var cmd = new NpgsqlCommand("SELECT subject , score , created_at FROM user_scores WHERE user_id = @user_id ORDER BY created_at DESC", conn);
                cmd.Parameters.AddWithValue("user_id", chatId);

                using (var reader = cmd.ExecuteReader()) {
                    if (!reader.HasRows) {
                        await _botClient.SendTextMessageAsync(chatId , "У вас нет результатов.");
                        return;
                    }

                    string resultsMessage = "Ваши результаты:\n\n";
                    resultsMessage += $"{"Дата", -15} {"Предмет", -15} {"Баллы", -10}\n"; // Заголовки

                    while (reader.Read()) {
                        DateTime createdAt = reader.GetDateTime(2);
                        string currentSubjectName= reader.GetString(0);
                        int score= reader.GetInt32(1);
                        resultsMessage += $"{createdAt.ToShortDateString(), -15} {currentSubjectName,-15} {score,-10}\n"; // Форматирование
                    }

                    resultsMessage += "\nВыберите действие:";
                    await _botClient.SendTextMessageAsync(chatId , resultsMessage.Trim(), replyMarkup: GetPostQuizMenu());
                }
            }
        }
    }
}
