#addin nuget:?package=Cake.Unity&version=0.9.0
#addin nuget:?package=WTelegramClient&version=3.3.3
#addin nuget:?package=FluentFTP&version=46.0.2
#addin nuget:?package=Cake.Git&version=3.0.0

using System.Security.Cryptography;
using System.ComponentModel.Design.Serialization;
using TL;
using FluentFTP;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Globalization;

string ProjectName = Context.Configuration.GetValue("Project_Name");
const string ArtifactsFolderPath = "./artifacts";

var opened = System.IO.File.Open(TelegramSessionPath, FileMode.OpenOrCreate);
var tgClient = new WTelegram.Client(TelegramConfig, opened);
var account = await tgClient.LoginUserIfNeeded();
var dialogs = await tgClient.Channels_GetAdminedPublicChannels();
var lastCommit = GitLog(new DirectoryPath("."), 1).First();

var target = Argument("target", SendBuildNotificationEndingTask);

#region Send-Build-Notification-Start

const string SendBuildNotificationStartingTask = "Send-Build-Notification-Start";

Task(SendBuildNotificationStartingTask)
    .Does(() => 
{
    WriteTelegramMessage(CreateStartingTelegramMessage());
});

async void WriteTelegramMessage(string message)
{
    var dialog = GetTargetChat();
    await tgClient.SendMessageAsync(dialog, message);
}

string CreateStartingTelegramMessage()
{
    var message = $"❗❗❗Начат билд по проекту {ProjectName}! " + 
        $"Произошедшие изменения: {lastCommit.Message}\n";
    return message;
}
#endregion

#region Clean-Artifacts

const string CleanArtifactsTask = "Clean-Artifacts";

Task(CleanArtifactsTask)
    .IsDependentOn(SendBuildNotificationStartingTask)
    .Does(() => 
{
    CleanDirectory(ArtifactsFolderPath);
});
#endregion

#region Build-WebGl

const string BuildWebGlTask = "Build-WebGL";
string UnityPath = Context.Configuration.GetValue("Unity_Path");
const string UnityBuildMethod = "BuilderScript.Editor.Builder.BuildWebGl";
string ProjectFolderPath;
if(System.IO.Directory.Exists("./src"))
{
    ProjectFolderPath = $"./src/{ProjectName}";
}
else 
{
    ProjectFolderPath = ".";
}

Task(BuildWebGlTask)
    .IsDependentOn(CleanArtifactsTask)
    .Does(() => 
{
    UnityEditor(
        CreateUnityEditorArguments(),
        new UnityEditorSettings 
        {
            RealTimeLog = true
        });
});

UnityEditorArguments CreateUnityEditorArguments()
{
    var arguments = new UnityEditorArguments
    {
        LogFile = ArtifactsFolderPath + "/unity.log",
        ExecuteMethod = UnityBuildMethod,
        BuildTarget = BuildTarget.WebGL,
        ProjectPath = ProjectFolderPath,
        ForceFree = true
    };
    arguments.Custom.BuildFolder = CreateBuildFolderName();
    return arguments;
}

string CreateBuildFolderName()
{
    var now = DateTime.Now;
    var culture = new CultureInfo("ru-RU");
    
    return $"{now.ToString("dd.MM.yyyy", culture)}_{ProjectName}_{now.ToString("hh.mm", culture)}";
}

#endregion

#region Upload-File

const string UploadFileTask = "Upload-File";
string SearchingBuildFolderPattern = @"^.{0,}" + ProjectName + ".{0,}$";

Task(UploadFileTask)
    .IsDependentOn(BuildWebGlTask)
    .Does(() => 
{
    var client = new FtpClient(
        FtpConfig("Ftp_Host"), 
        FtpConfig("Ftp_Login"), 
        FtpConfig("Ftp_Password"));
    client.Connect();
    var targetDirectory = GetLastArtifactDirectory();        
    client.DeleteDirectory($"/{ProjectName}");
    client.CreateDirectory($"/{ProjectName}");
    client.UploadDirectory(targetDirectory, $"/{ProjectName}");
});

string GetLastArtifactDirectory() => 
    System.IO.Directory.EnumerateDirectories(".\\artifacts")
        .First(directory => Regex.IsMatch(directory, SearchingBuildFolderPattern));

string FtpConfig(string key) => key switch
{
    "Ftp_Host" => Context.Configuration.GetValue("Ftp_Host"),
    "Ftp_Login" => Context.Configuration.GetValue("Ftp_Login"),
    "Ftp_Password" => Context.Configuration.GetValue("Ftp_Password")
};

#endregion

#region Zip-Artifacts

const string ZipArtifactsTask = "Zip-Artifacts";
string pathToZippedBuild;

Task(ZipArtifactsTask)
    .IsDependentOn(UploadFileTask)
    .Does(() => 
{
    DirectoryPath artifactsDirectory = DirectoryPath.FromString(GetLastArtifactDirectory());
    var archivePath = System.IO.Path.Combine(ArtifactsFolderPath, CreateBuildFolderName() + ".zip");
    pathToZippedBuild = archivePath;
    Zip(artifactsDirectory, archivePath);
});

#endregion

#region Send-Build-Notification-Ending

const string SendBuildNotificationEndingTask = "Send-Build-Notification-Ending";
const string TelegramSessionPathParameter = "Telegram_SessionPath";
const string TelegramChatIdParameter = "Telegram_ChatId";
const string TelegramApiIdParameter = "Telegram_ApiId";
const string TelegramApiHashParameter = "Telegram_ApiHash";
const string TelegramPhoneNumberParameter = "Telegram_PhoneNumber";

string TelegramSessionPath => Context.Configuration.GetValue(TelegramSessionPathParameter);

Task(SendBuildNotificationEndingTask)
    .IsDependentOn(ZipArtifactsTask)
    .Does(async () => 
{
    var handle = await tgClient.UploadFileAsync(pathToZippedBuild);
    var targetChat = GetTargetChat();
    var message = await tgClient.SendMediaAsync(targetChat, CreateEndingTelegramMessage(), handle);
});

ChatBase GetTargetChat()
{
    if(Int64.TryParse(Context.Configuration.GetValue(TelegramChatIdParameter), out Int64 chatId))
    {
        return dialogs.chats[chatId];
    }
    
    var targetDialog = dialogs.chats.FirstOrDefault(x => x.Value.Title == Context.Configuration.GetValue(TelegramChatIdParameter)).Value;
    if(targetDialog == null)
        throw new Exception("Not found required chat");
    return targetDialog;
}

string TelegramConfig(string what)
{
    switch (what)
    {
        case "api_id": return Context.Configuration.GetValue(TelegramApiIdParameter);
        case "api_hash": return Context.Configuration.GetValue(TelegramApiHashParameter);
        case "phone_number": return Context.Configuration.GetValue(TelegramPhoneNumberParameter);
        case "verification_code": Console.Write("Code: "); return Console.ReadLine();
        // Есть включена двойная аутентификация на аккаунте это может понадобиться:
        case "first_name": return "John";
        case "last_name": return "Doe";
        case "password": return "secret!";
        default: return null;                  
    }
}

string CreateEndingTelegramMessage()
{
    var lastCommit = GitLog(new DirectoryPath("."), 1).First();
    var message = $"❗❗❗Новый билд по проекту {ProjectName}! " + 
        $"Произошедшие изменения: {lastCommit.Message}\n" +
        $"Демонстрационная ссылка: https://immgames.ru/Games/Wolf/{ProjectName}. "+
        $"Внимание! После следующего обновления эта версия игры \"сгорит\" из ссылки.\n" +
        $"Прилагается архив с билдом:";
    return message;
}
#endregion

RunTarget(target);
