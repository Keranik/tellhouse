namespace TellhouseBackup;

// Recovery phrase encoding: one word per byte of the recovery secret,
// plus one checksum word. The list holds exactly 256 distinct words so
// every byte value maps to exactly one word and back. SelfTest asserts
// the count and the distinctness, so a list edit that breaks either
// fails the checks instead of weakening a household phrase.
public static class WordList
{
    public static readonly string[] Words = new[]
    {
        "apple","apron","arrow","aspen","attic","anchor","almond","amber","anvil","arcade","archer","atlas","atom","audio","aunt","award",
        "bacon","bagel","baker","bamboo","banner","basket","beacon","beaver","berry","birch","blanket","breeze","brick","bridge","brook","butter",
        "cabin","cactus","candle","canyon","carpet","carrot","castle","cedar","chalk","cherry","chimney","cinnamon","clover","compass","copper","coral",
        "daisy","dawn","deer","delta","desert","diamond","dinner","dolphin","drawer","dream","drum","duck","dune","dusk","doctor","door",
        "eagle","earth","east","echo","elder","elm","ember","emerald","engine","evening","elbow","entrance","envelope","essay","ethics","exile",
        "falcon","feather","fennel","fern","ferry","field","fig","finch","finger","flame","flax","fleece","flint","flour","flower","flute",
        "garden","garlic","gate","glacier","glass","glove","goat","gold","goose","grain","grape","grass","gravel","green","grove","guitar",
        "harbor","harvest","hatchet","hawk","hazel","hearth","heather","hedge","heron","hill","hinge","honey","hood","hope","horn","horse",
        "ice","icon","igloo","image","inch","index","ink","inn","input","insect","iron","island","item","ivory","ivy","idea",
        "jacket","jaguar","jam","jar","jazz","jeans","jewel","job","joke","journal","journey","joy","judge","juice","jump","jungle",
        "keen","kettle","key","kick","kidney","kind","king","kiss","kite","kitten","knee","knife","knock","knot","koala","kennel",
        "label","labor","lace","ladder","lady","lake","lamp","lance","lantern","larch","lark","laser","latch","laugh","laundry","lava",
        "magnet","maid","mail","major","mammal","mango","manner","manor","maple","marble","march","marine","market","marrow","marsh","mask",
        "nail","name","napkin","narrow","nation","native","nature","navy","near","neck","needle","nest","net","neutral","night","noble",
        "oak","oasis","oath","object","ocean","odd","offer","office","oil","olive","once","onion","only","open","opera","option",
        "pace","pack","paddle","page","pail","paint","pair","palace","palm","panda","panel","panic","paper","parade","parcel","pardon",
    };

    public static void Validate()
    {
        if (Words.Length != 256) throw new InvalidOperationException("word list must hold 256 words");
        if (Words.Distinct().Count() != 256) throw new InvalidOperationException("word list words must be distinct");
    }

    public static string Encode(byte[] secret)
    {
        Validate();
        var words = new List<string>();
        foreach (var b in secret) words.Add(Words[b]);
        var check = System.Security.Cryptography.SHA256.HashData(secret)[0];
        words.Add(Words[check]);
        return string.Join(" ", words);
    }

    public static byte[] Decode(string phrase)
    {
        Validate();
        var parts = phrase.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) throw new InvalidOperationException("recovery phrase is too short");
        var bytes = new byte[parts.Length - 1];
        for (int i = 0; i < parts.Length; i++)
        {
            int idx = Array.IndexOf(Words, parts[i].ToLowerInvariant());
            if (idx < 0) throw new InvalidOperationException("recovery phrase holds a word that is not in the list: " + parts[i]);
            if (i < bytes.Length) bytes[i] = (byte)idx;
            else
            {
                var check = System.Security.Cryptography.SHA256.HashData(bytes)[0];
                if (check != (byte)idx) throw new InvalidOperationException("recovery phrase checksum word does not match");
            }
        }
        return bytes;
    }
}
