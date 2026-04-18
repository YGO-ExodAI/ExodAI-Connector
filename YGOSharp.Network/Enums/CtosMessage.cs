namespace YGOSharp.Network.Enums
{
    public enum CtosMessage
    {
        Response = 0x1,
        UpdateDeck = 0x2,
        HandResult = 0x3,
        TpResult = 0x4,
        PlayerInfo = 0x10,
        CreateGame = 0x11,
        JoinGame = 0x12,
        LeaveGame = 0x13,
        Surrender = 0x14,
        TimeConfirm = 0x15,
        Chat = 0x16,
        HsToDuelist = 0x20,
        HsToObserver = 0x21,
        HsReady = 0x22,
        HsNotReady = 0x23,
        HsKick = 0x24,
        HsStart = 0x25,
        RematchResponse = 0xf0,
        // ExodAI extension. Sent right before CTOS.Response at each top-level
        // bot decision. Payload: uint16 length + UTF-8 JSON bytes. The server
        // injects a matching MSG_AI_THOUGHT into the replay stream.
        AiThought = 0x30
    }
}
