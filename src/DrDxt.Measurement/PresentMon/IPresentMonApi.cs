namespace DrDxt.Measurement.PresentMon;

/// <summary>
/// Couture mince et testable sur les P/Invoke bruts de PresentMonAPI2.dll.
/// <see cref="PresentMonSession"/> la consomme pour que sa logique de capture / d'analyse se teste
/// avec un faux qui rend des octets de blob préparés ; la seule frontière sans test unitaire est le
/// vrai <see cref="PresentMonApi2"/> (confirmé par un smoke test manuel en jeu).
/// Chaque méthode rend le <see cref="PM_STATUS"/> brut de l'appel natif.
/// </summary>
internal interface IPresentMonApi
{
    PM_STATUS OpenSession(out nint session);
    PM_STATUS CloseSession(nint session);
    PM_STATUS StartTracking(nint session, uint pid);
    PM_STATUS StopTracking(nint session, uint pid);

    /// <summary>
    /// Enregistre une requête d'images. En cas de succès, <paramref name="query"/> tient le handle de
    /// la requête, <paramref name="blobSize"/> la taille en octets du blob d'une image, et chaque
    /// entrée de <paramref name="elements"/> a ses <c>dataOffset</c>/<c>dataSize</c> remplis.
    /// </summary>
    PM_STATUS RegisterFrameQuery(nint session, PM_QUERY_ELEMENT[] elements, out nint query, out uint blobSize);

    /// <summary>
    /// Consomme jusqu'à <paramref name="maxFrames"/> images dans <paramref name="blobs"/> ;
    /// <paramref name="framesRead"/> dit combien ont vraiment été écrites.
    /// </summary>
    PM_STATUS ConsumeFrames(nint query, uint pid, byte[] blobs, uint maxFrames, out uint framesRead);

    PM_STATUS FreeFrameQuery(nint query);
}
