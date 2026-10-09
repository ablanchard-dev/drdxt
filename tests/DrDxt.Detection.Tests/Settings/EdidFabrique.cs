namespace DrDxt.Detection.Tests.Settings;

/// <summary>
/// Fabrique un EDID minimal mais VALIDE pour les cas qu'aucun écran de la maison ne porte.
///
/// Les cas NORMAUX se testent sur de vrais EDID lus dans le registre (cf.
/// <see cref="EdidVrrCapabiliteTests"/>). Cette fabrique ne sert qu'aux cas LIMITES — un bloc
/// HDMI Forum aux bornes invalides, par exemple — qu'on ne peut pas se procurer autrement. Un
/// produit qui ne se teste que sur des octets qu'il a lui-même fabriqués ne prouve rien d'autre
/// que sa propre fabrication ; c'est la faute que cette boucle a corrigée quatre fois ailleurs.
/// </summary>
internal static class EdidFabrique
{
    /// <summary>Un EDID de 256 octets dont l'extension CTA-861 porte un HDMI Forum VSDB.</summary>
    public static byte[] AvecHdmiForum(int vrrMin, int vrrMax)
    {
        var edid = new byte[256];

        // En-tete EDID 1.x, le seul champ que le lecteur exige pour accepter de lire.
        byte[] entete = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        entete.CopyTo(edid, 0);
        edid[126] = 1; // une extension

        // Extension CTA-861 : tag 0x02, revision 3, et les DTD commencent apres nos data blocks.
        edid[128] = 0x02;
        edid[129] = 0x03;

        // Les data blocks commencent a l'octet 4 DE L'EXTENSION, soit edid[132]. Cette fabrique
        // s'est trompee d'un octet au premier jet, et c'est elle que le test a accusee, pas le
        // lecteur : les deux vrais EDID passaient deja. Un rappel utile — une fixture fabriquee
        // est du code comme un autre, et elle se debogue.
        const int debutBlocs = 128 + 4;
        const int charge = 11;
        edid[debutBlocs] = (byte)(3 << 5 | charge);
        // OUI HDMI Forum (0xC45DD8), petit-boutiste dans l'EDID.
        edid[debutBlocs + 1] = 0xD8;
        edid[debutBlocs + 2] = 0x5D;
        edid[debutBlocs + 3] = 0xC4;
        // Octets 9 et 10 de la charge utile : VRRmin (6 bits bas) et VRRmax (+ 2 bits hauts).
        edid[debutBlocs + 1 + 9] = (byte)((vrrMin & 0x3F) | ((vrrMax >> 8 & 0x03) << 6));
        edid[debutBlocs + 1 + 10] = (byte)(vrrMax & 0xFF);

        edid[130] = (byte)(4 + 1 + charge); // ou commencent les DTD = apres le bloc
        return edid;
    }
}
