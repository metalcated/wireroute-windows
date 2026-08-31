using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using WireRoute.Core.Manager;
using WireRoute.Core.Routing;

namespace WireRoute.Core.Tests;

[TestClass]
public sealed class ManagerProtocolTests
{
    [TestMethod]
    public void SerializationUsesStableCamelCaseNamesAndStringEnums()
    {
        var response = ManagerResponse.Success(
            7,
            new ManagerProfileSummary("office", ManagerTunnelState.Started, TunnelRouteMode.Full));

        var json = Encoding.UTF8.GetString(ManagerProtocolJson.Serialize(response));

        StringAssert.Contains(json, "\"requestId\":7");
        StringAssert.Contains(json, "\"state\":\"started\"");
        StringAssert.Contains(json, "\"detectedRouteMode\":\"full\"");
        Assert.IsFalse(json.Contains("RequestId", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FrameCodecRoundTripsProtocolMessages()
    {
        await using var stream = new MemoryStream();
        var request = ManagerRequest.Create(12, ManagerMethods.GetProfile, new ManagerGetProfileRequest("office"));

        await ManagerFrameCodec.WriteAsync(stream, request);
        stream.Position = 0;
        var decoded = await ManagerFrameCodec.ReadAsync<ManagerRequest>(stream);

        Assert.AreEqual(ManagerProtocol.CurrentVersion, decoded.Version);
        Assert.AreEqual(12, decoded.RequestId);
        Assert.AreEqual(ManagerMethods.GetProfile, decoded.Method);
        Assert.AreEqual("office", decoded.Parameters?.Deserialize<ManagerGetProfileRequest>(ManagerProtocolJson.Options)?.Name);
    }

    [TestMethod]
    public async Task FrameCodecRejectsOversizedWrites()
    {
        await using var stream = new MemoryStream();
        var oversized = new string('x', ManagerProtocol.MaximumFrameLength);

        await Assert.ThrowsExactlyAsync<ManagerProtocolException>(async () =>
            await ManagerFrameCodec.WriteAsync(stream, oversized));
    }

    [TestMethod]
    public async Task FrameCodecRejectsInvalidAndTruncatedReads()
    {
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ManagerProtocol.MaximumFrameLength + 1u);
        await using var oversized = new MemoryStream(header);
        await Assert.ThrowsExactlyAsync<ManagerProtocolException>(async () =>
            await ManagerFrameCodec.ReadAsync<ManagerRequest>(oversized));

        await using var truncated = new MemoryStream([5, 0, 0, 0, (byte)'{']);
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () =>
            await ManagerFrameCodec.ReadAsync<ManagerRequest>(truncated));
    }

    [TestMethod]
    public async Task ClientNegotiatesHelloAndCorrelatesTheResponse()
    {
        var capabilities = new ManagerCapabilities(true, true, true, false, false, false);
        var helloResponse = new ManagerHelloResponse(
            ManagerProtocol.Name,
            ManagerProtocol.CurrentVersion,
            "0.1.0",
            capabilities);
        await using var responseStream = await FramedResponseStreamAsync(ManagerResponse.Success(1, helloResponse));
        await using var requestStream = new MemoryStream();
        await using var eventStream = new MemoryStream();
        await using var client = new ManagerProtocolClient(responseStream, requestStream, eventStream, leaveOpen: true);

        var response = await client.HelloAsync("0.1.0", "x64");

        Assert.AreEqual(ManagerProtocol.Name, response.Protocol);
        Assert.IsTrue(response.Capabilities.CanListProfiles);
        Assert.IsFalse(response.Capabilities.CanImportProfiles);
        requestStream.Position = 0;
        var request = await ManagerFrameCodec.ReadAsync<ManagerRequest>(requestStream);
        Assert.AreEqual(1, request.RequestId);
        Assert.AreEqual(ManagerMethods.Hello, request.Method);
        var helloRequest = request.Parameters?.Deserialize<ManagerHelloRequest>(ManagerProtocolJson.Options);
        Assert.AreEqual("x64", helloRequest?.Architecture);
    }

    [TestMethod]
    public async Task ClientRejectsMismatchedResponseIdsAndRemoteErrors()
    {
        var hello = new ManagerHelloResponse(
            ManagerProtocol.Name,
            ManagerProtocol.CurrentVersion,
            "0.1.0",
            new ManagerCapabilities(true, true, true, false, false, false));
        await using var mismatchedResponse = await FramedResponseStreamAsync(
            ManagerResponse.Success(1, hello),
            ManagerResponse.Success(99, new ManagerGetTunnelStateResponse("office", ManagerTunnelState.Stopped)));
        await using var firstRequestStream = new MemoryStream();
        await using var firstEventStream = new MemoryStream();
        await using (var client = new ManagerProtocolClient(
            mismatchedResponse,
            firstRequestStream,
            firstEventStream,
            leaveOpen: true))
        {
            await client.HelloAsync("0.1.0", "x64");
            await Assert.ThrowsExactlyAsync<ManagerProtocolException>(async () =>
                await client.RequestAsync<ManagerGetTunnelStateRequest, ManagerGetTunnelStateResponse>(
                    ManagerMethods.GetTunnelState,
                    new ManagerGetTunnelStateRequest("office")));
            await Assert.ThrowsExactlyAsync<ManagerProtocolException>(async () =>
                await client.RequestAsync<ManagerGetTunnelStateRequest, ManagerGetTunnelStateResponse>(
                    ManagerMethods.GetTunnelState,
                    new ManagerGetTunnelStateRequest("office")));
        }

        await using var failedResponse = await FramedResponseStreamAsync(
            ManagerResponse.Success(1, hello),
            ManagerResponse.Failure(2, "profileNotFound", "The profile does not exist."),
            ManagerResponse.Success(
                3,
                new ManagerGetTunnelStateResponse("office", ManagerTunnelState.Stopped)));
        await using var secondRequestStream = new MemoryStream();
        await using var secondEventStream = new MemoryStream();
        await using var failedClient = new ManagerProtocolClient(
            failedResponse,
            secondRequestStream,
            secondEventStream,
            leaveOpen: true);
        await failedClient.HelloAsync("0.1.0", "x64");
        var exception = await Assert.ThrowsExactlyAsync<ManagerRemoteException>(async () =>
            await failedClient.RequestAsync<ManagerGetProfileRequest, ManagerProfileDetail>(
                ManagerMethods.GetProfile,
                new ManagerGetProfileRequest("missing")));
        Assert.AreEqual("profileNotFound", exception.Code);

        var recovered = await failedClient.RequestAsync<
            ManagerGetTunnelStateRequest,
            ManagerGetTunnelStateResponse>(
                ManagerMethods.GetTunnelState,
                new ManagerGetTunnelStateRequest("office"));
        Assert.AreEqual(ManagerTunnelState.Stopped, recovered.State);
    }

    [TestMethod]
    public async Task ClientRequiresExactlyOneSuccessfulHelloAttempt()
    {
        await using var responseStream = new MemoryStream();
        await using var requestStream = new MemoryStream();
        await using var eventStream = new MemoryStream();
        await using var client = new ManagerProtocolClient(
            responseStream,
            requestStream,
            eventStream,
            leaveOpen: true);

        await Assert.ThrowsExactlyAsync<ManagerProtocolException>(async () =>
            await client.RequestAsync<ManagerGetProfileRequest, ManagerProfileDetail>(
                ManagerMethods.GetProfile,
                new ManagerGetProfileRequest("office")));

        await using var helloResponseStream = await FramedResponseStreamAsync(
            ManagerResponse.Success(
                1,
                new ManagerHelloResponse(
                    ManagerProtocol.Name,
                    ManagerProtocol.CurrentVersion,
                    "0.1.0",
                    new ManagerCapabilities(true, true, true, false, false, false))));
        await using var secondRequestStream = new MemoryStream();
        await using var secondEventStream = new MemoryStream();
        await using var negotiatedClient = new ManagerProtocolClient(
            helloResponseStream,
            secondRequestStream,
            secondEventStream,
            leaveOpen: true);
        await negotiatedClient.HelloAsync("0.1.0", "x64");
        await Assert.ThrowsExactlyAsync<ManagerProtocolException>(async () =>
            await negotiatedClient.HelloAsync("0.1.0", "x64"));
    }

    [TestMethod]
    public void ResponseRejectsSimultaneousResultAndError()
    {
        var response = new ManagerResponse(
            ManagerProtocol.CurrentVersion,
            1,
            ManagerProtocolJson.ToElement(new ManagerEmpty()),
            new ManagerError("invalid", "Invalid response."));

        Assert.ThrowsExactly<ManagerProtocolException>(() => response.GetRequiredResult<ManagerEmpty>());
    }

    [TestMethod]
    public async Task ClientRejectsIncompatibleHelloSelection()
    {
        await using var responseStream = await FramedResponseStreamAsync(
            ManagerResponse.Success(
                1,
                new ManagerHelloResponse(
                    ManagerProtocol.Name,
                    ManagerProtocol.CurrentVersion + 1,
                    "0.1.0",
                    new ManagerCapabilities(true, true, true, false, false, false))));
        await using var requestStream = new MemoryStream();
        await using var eventStream = new MemoryStream();
        await using var client = new ManagerProtocolClient(
            responseStream,
            requestStream,
            eventStream,
            leaveOpen: true);

        await Assert.ThrowsExactlyAsync<ManagerProtocolException>(async () =>
            await client.HelloAsync("0.1.0", "x64"));
    }

    [TestMethod]
    public async Task ClientRequiresStrictlyIncreasingEventSequences()
    {
        await using var eventStream = new MemoryStream();
        var payload = ManagerProtocolJson.ToElement(new ManagerProfilesChangedEvent(["office"]));
        await ManagerFrameCodec.WriteAsync(
            eventStream,
            new ManagerEvent(ManagerProtocol.CurrentVersion, 1, ManagerEvents.ProfilesChanged, payload));
        await ManagerFrameCodec.WriteAsync(
            eventStream,
            new ManagerEvent(ManagerProtocol.CurrentVersion, 1, ManagerEvents.ProfilesChanged, payload));
        eventStream.Position = 0;
        await using var responseStream = new MemoryStream();
        await using var requestStream = new MemoryStream();
        await using var client = new ManagerProtocolClient(
            responseStream,
            requestStream,
            eventStream,
            leaveOpen: true);

        var first = await client.ReadEventAsync();
        Assert.AreEqual(1, first.Sequence);
        CollectionAssert.AreEqual(
            new[] { "office" },
            first.GetRequiredPayload<ManagerProfilesChangedEvent>().ProfileNames.ToArray());
        await Assert.ThrowsExactlyAsync<ManagerProtocolException>(async () => await client.ReadEventAsync());
    }

    private static async Task<MemoryStream> FramedResponseStreamAsync(params ManagerResponse[] responses)
    {
        var stream = new MemoryStream();
        foreach (var response in responses)
        {
            await ManagerFrameCodec.WriteAsync(stream, response);
        }

        stream.Position = 0;
        return stream;
    }
}
