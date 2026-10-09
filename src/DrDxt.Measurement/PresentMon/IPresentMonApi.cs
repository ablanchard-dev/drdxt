namespace DrDxt.Measurement.PresentMon;

/// <summary>
/// Thin, testable seam over the raw PresentMonAPI2.dll P/Invokes.
/// <see cref="PresentMonSession"/> consumes this so its capture/parse logic can be
/// unit-tested with a fake that returns canned blob bytes — the only un-unit-tested
/// boundary is the real <see cref="PresentMonApi2"/> (confirmed by a manual game smoke).
/// Every method returns the raw <see cref="PM_STATUS"/> from the native call.
/// </summary>
internal interface IPresentMonApi
{
    PM_STATUS OpenSession(out nint session);
    PM_STATUS CloseSession(nint session);
    PM_STATUS StartTracking(nint session, uint pid);
    PM_STATUS StopTracking(nint session, uint pid);

    /// <summary>
    /// Registers a frame query. On success, <paramref name="query"/> holds the query handle,
    /// <paramref name="blobSize"/> the byte size of one frame's blob, and each entry of
    /// <paramref name="elements"/> has its <c>dataOffset</c>/<c>dataSize</c> filled in.
    /// </summary>
    PM_STATUS RegisterFrameQuery(nint session, PM_QUERY_ELEMENT[] elements, out nint query, out uint blobSize);

    /// <summary>
    /// Consumes up to <paramref name="maxFrames"/> frames into <paramref name="blobs"/>;
    /// <paramref name="framesRead"/> reports how many were actually written.
    /// </summary>
    PM_STATUS ConsumeFrames(nint query, uint pid, byte[] blobs, uint maxFrames, out uint framesRead);

    PM_STATUS FreeFrameQuery(nint query);
}
