namespace MessageLink.Presentation.Services;

public static partial class MergeSuggestions
{
    // English given names and their usual short forms. A name in several families (Chris, Alex, Sam, Pat, Ted, Harry)
    // belongs to each, so "Chris" is like both Christopher and Christine while Christopher and Christine are not alike.
    // Names from other languages match only when written the same, accents aside.
    private static readonly string[][] Families =
    [
        ["james", "jim", "jimmy", "jamie", "jimbo"], ["robert", "rob", "robbie", "bob", "bobby", "bert", "robby"],
        ["william", "will", "bill", "billy", "liam", "willie", "willy"], ["elizabeth", "liz", "lizzie", "beth", "betty", "eliza", "libby", "lisa", "betsy"],
        ["katherine", "catherine", "kathryn", "kate", "katie", "kathy", "cathy", "kat", "kay"], ["michael", "mike", "mikey", "mick", "mickey"],
        ["margaret", "maggie", "meg", "peggy", "marge", "greta"], ["richard", "rick", "ricky", "rich", "dick", "richie"],
        ["joseph", "joe", "joey"], ["thomas", "tom", "tommy"], ["jonathan", "john", "jon", "johnny", "jack"],
        ["christopher", "chris", "kit", "topher"], ["christine", "christina", "chris", "chrissy", "tina", "kristin", "kristen"],
        ["alexander", "alex", "al", "xander", "sasha", "lex"], ["alexandra", "alex", "alexa", "lexi", "sandra", "sasha"],
        ["samuel", "sam", "sammy"], ["samantha", "sam", "sammy"], ["patrick", "pat", "paddy"], ["patricia", "pat", "patty", "trish", "tricia"],
        ["edward", "ed", "eddie", "ted", "teddy", "ned"], ["theodore", "ted", "teddy", "theo"], ["harold", "harry", "hal"], ["henry", "harry", "hank", "hal"],
        ["daniel", "dan", "danny"], ["david", "dave", "davey"], ["matthew", "matt", "matty"], ["andrew", "andy", "drew"],
        ["anthony", "tony"], ["benjamin", "ben", "benny", "benji"], ["nicholas", "nick", "nicky", "nico"], ["nicole", "nicky", "nikki", "nic"],
        ["steven", "stephen", "steve", "stevie"], ["stephanie", "steph", "stephie"], ["charles", "charlie", "chuck", "chas"],
        ["charlotte", "charlie", "lottie"], ["jennifer", "jen", "jenny", "jenn"], ["jessica", "jess", "jessie"], ["rebecca", "becky", "becca", "bec"],
        ["deborah", "debra", "deb", "debbie"], ["susan", "sue", "susie", "suzy"], ["victoria", "vicky", "tori", "vic"], ["gregory", "greg"],
        ["jeffrey", "geoffrey", "jeff", "geoff"], ["kenneth", "ken", "kenny"], ["ronald", "ron", "ronnie"], ["donald", "don", "donny"],
        ["timothy", "tim", "timmy"], ["peter", "pete"], ["philip", "phillip", "phil"], ["raymond", "ray"], ["lawrence", "laurence", "larry"],
        ["gerald", "gerry", "jerry"], ["jeremy", "jerry"], ["frederick", "fred", "freddie", "freddy"], ["francis", "frank", "frankie", "fran"],
        ["frances", "fran", "frannie", "francie"], ["zachary", "zach", "zack"], ["joshua", "josh"], ["jacob", "jake"], ["nathaniel", "nathan", "nate", "nat"],
        ["abigail", "abby", "abbie"], ["amanda", "mandy", "manda"], ["barbara", "barb", "barbie"], ["dorothy", "dot", "dottie", "dolly"],
        ["kimberly", "kim"], ["megan", "meg"], ["pamela", "pam"], ["sandra", "sandy"], ["cynthia", "cindy"], ["judith", "judy", "jude"],
        ["douglas", "doug"], ["leonard", "leo", "len", "lenny"], ["walter", "walt", "wally"], ["albert", "al", "bert", "bertie"],
        ["gabriel", "gabe"], ["gabrielle", "gabby", "gabi"], ["madeline", "madeleine", "maddie", "maddy"], ["benedict", "ben"],
    ];

    private static readonly Dictionary<string, HashSet<int>> Nicknames = Build();
    private static Dictionary<string, HashSet<int>> Build()
    {
        var names = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var family = 0; family < Families.Length; family++)
            foreach (var name in Families[family])
            {
                if (!names.TryGetValue(name, out var families)) names[name] = families = [];
                families.Add(family);
            }
        return names;
    }
}
