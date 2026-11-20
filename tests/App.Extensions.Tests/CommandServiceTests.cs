using App.Core.Errors;
using App.Extensions.Commands;

namespace App.Extensions.Tests;

public sealed class CommandServiceTests
{
    [Fact]
    public async Task Register_ThenExecute_RunsHandler()
    {
        var service = new CommandService();
        bool executed = false;

        service.Register("app.test", "Test", _ =>
        {
            executed = true;
            return Task.CompletedTask;
        });

        await service.ExecuteAsync("app.test");

        Assert.True(executed);
        Assert.Equal("app.test", Assert.Single(service.Commands).Id);
    }

    [Fact]
    public void Register_SameIdTwice_ThrowsAlreadyExists()
    {
        var service = new CommandService();
        service.Register("app.test", "Test", _ => Task.CompletedTask);

        AppException ex = Assert.Throws<AppException>(
            () => service.Register("app.test", "Test", _ => Task.CompletedTask));

        Assert.Equal(ErrorCode.AlreadyExists, ex.Code);
    }

    [Fact]
    public void Dispose_RemovesCommandAndRaisesEvent()
    {
        var service = new CommandService();
        IDisposable registration = service.Register("app.test", "Test", _ => Task.CompletedTask);

        int changes = 0;
        service.CommandsChanged += (_, _) => changes++;

        registration.Dispose();
        registration.Dispose();

        Assert.Empty(service.Commands);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Execute_UnknownCommand_ThrowsNotFound()
    {
        var service = new CommandService();

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => service.ExecuteAsync("inconnue"));

        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Execute_HandlerThrows_WrapsInAppException()
    {
        var service = new CommandService();
        service.Register("app.boom", "Boom", _ => throw new InvalidOperationException("boom"));

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => service.ExecuteAsync("app.boom"));

        Assert.Equal(ErrorCode.Unknown, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
}
