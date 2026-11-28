namespace MessageLink.Presentation.Services;

// The curated set itself. Each line is glyph|name|space-separated search terms, and a glyph prefixed with ~ takes a
// skin tone. Names are the ones a person would search for rather than the full Unicode names.
public static partial class EmojiCatalog
{
    private const string Smileys = """
😀|grinning face|smile happy grin
😃|grinning face with big eyes|smile happy
😄|grinning face with smiling eyes|smile happy
😁|beaming face with smiling eyes|grin smile
😆|grinning squinting face|laugh haha
😅|grinning face with sweat|laugh relief phew
🤣|rolling on the floor laughing|rofl lol laugh
😂|face with tears of joy|lol laugh crying
🙂|slightly smiling face|smile
🙃|upside-down face|silly sarcasm
🫠|melting face|hot embarrassed
😉|winking face|wink flirt
😊|smiling face with smiling eyes|blush happy
😇|smiling face with halo|angel innocent
🥰|smiling face with hearts|love adore
😍|smiling face with heart-eyes|love crush
🤩|star-struck|wow amazed stars
😘|face blowing a kiss|kiss love
😗|kissing face|kiss
😚|kissing face with closed eyes|kiss
😙|kissing face with smiling eyes|kiss
🥲|smiling face with tear|touched grateful
😋|face savoring food|yum tasty delicious
😛|face with tongue|tongue playful
😜|winking face with tongue|silly playful
🤪|zany face|crazy silly wild
😝|squinting face with tongue|tongue silly
🤑|money-mouth face|rich money
🤗|smiling face with open hands|hug hugs
🤭|face with hand over mouth|oops giggle
🫢|face with open eyes and hand over mouth|shock gasp
🤫|shushing face|quiet secret shh
🤔|thinking face|hmm think wondering
🫡|saluting face|salute yes
🤐|zipper-mouth face|quiet secret
🤨|face with raised eyebrow|suspicious doubt really
😐|neutral face|meh blank
😑|expressionless face|blank meh
😶|face without mouth|speechless silent
🫥|dotted line face|invisible hiding
😏|smirking face|smirk sly
😒|unamused face|annoyed meh
🙄|face with rolling eyes|eyeroll annoyed
😬|grimacing face|awkward yikes
🤥|lying face|liar
😮‍💨|face exhaling|sigh relief
😌|relieved face|calm relief
😔|pensive face|sad down
😪|sleepy face|tired sleep
🤤|drooling face|drool
😴|sleeping face|sleep zzz
😷|face with medical mask|sick mask
🤒|face with thermometer|sick fever
🤕|face with head-bandage|hurt injured
🤢|nauseated face|sick gross
🤮|face vomiting|sick puke
🤧|sneezing face|sneeze sick
🥵|hot face|hot sweating
🥶|cold face|cold freezing
🥴|woozy face|dizzy drunk
😵|face with crossed-out eyes|dizzy dead
😵‍💫|face with spiral eyes|dizzy confused
🤯|exploding head|mind blown shock
🤠|cowboy hat face|cowboy
🥳|partying face|party celebrate birthday
🥸|disguised face|disguise
😎|smiling face with sunglasses|cool sunglasses
🤓|nerd face|nerd geek
🧐|face with monocle|inspect curious
😕|confused face|confused
🫤|face with diagonal mouth|unsure meh
😟|worried face|worried
🙁|slightly frowning face|sad frown
😮|face with open mouth|wow surprise
😯|hushed face|surprise
😲|astonished face|shock wow
😳|flushed face|embarrassed blush
🥺|pleading face|please beg puppy
🥹|face holding back tears|emotional tears
😦|frowning face with open mouth|sad
😧|anguished face|shock
😨|fearful face|scared
😰|anxious face with sweat|nervous
😥|sad but relieved face|phew sad
😢|crying face|cry sad tear
😭|loudly crying face|sob cry bawling
😱|face screaming in fear|scream shock
😖|confounded face|frustrated
😣|persevering face|struggle
😞|disappointed face|sad
😓|downcast face with sweat|sad sweat
😩|weary face|tired exhausted
😫|tired face|tired exhausted
🥱|yawning face|tired bored
😤|face with steam from nose|triumph determined
😡|enraged face|angry mad rage
😠|angry face|angry mad
🤬|face with symbols on mouth|swearing angry cursing
😈|smiling face with horns|devil mischief
👿|angry face with horns|devil
💀|skull|dead skull dying
☠️|skull and crossbones|dead danger poison
💩|pile of poo|poop
🤡|clown face|clown
👻|ghost|ghost halloween boo
👽|alien|alien ufo
🤖|robot|robot bot
😺|grinning cat|cat
😸|grinning cat with smiling eyes|cat
😹|cat with tears of joy|cat laugh
😻|smiling cat with heart-eyes|cat love
😼|cat with wry smile|cat smirk
😽|kissing cat|cat kiss
🙀|weary cat|cat shock
😿|crying cat|cat cry
😾|pouting cat|cat angry
""";

    private const string People = """
~👋|waving hand|wave hi bye hello
~🤚|raised back of hand|hand
~🫱|rightwards hand|hand reach
~🫲|leftwards hand|hand reach
~🫳|palm down hand|drop dismiss
~🫴|palm up hand|offer beckon
~🫷|leftwards pushing hand|stop wait high five
~🫸|rightwards pushing hand|stop wait high five
~🖐️|hand with fingers splayed|hand five
~✋|raised hand|hand stop
~🖖|vulcan salute|spock
~👌|OK hand|ok perfect
~🤌|pinched fingers|italian gesture
~🤏|pinching hand|small little
~✌️|victory hand|peace two
~🤞|crossed fingers|luck hope
~🫰|hand with index finger and thumb crossed|money love
~🤟|love-you gesture|love
~🤘|sign of the horns|rock metal
~🤙|call me hand|call shaka
~👈|backhand index pointing left|point left
~👉|backhand index pointing right|point right
~👆|backhand index pointing up|point up this
~👇|backhand index pointing down|point down
~☝️|index pointing up|point up one
~🫵|index pointing at the viewer|you point
~🖕|middle finger|flip off rude
~👍|thumbs up|yes like approve good
~👎|thumbs down|no dislike bad
~✊|raised fist|fist power
~👊|oncoming fist|punch bump
~🤛|left-facing fist|fist bump
~🤜|right-facing fist|fist bump
~👏|clapping hands|clap applause bravo
~🙌|raising hands|celebrate praise hooray
~🫶|heart hands|love heart
~👐|open hands|hands open
~🤲|palms up together|pray please
~🤝|handshake|deal agree shake
~🙏|folded hands|pray thanks please
~💪|flexed biceps|strong muscle gym
~🦵|leg|leg kick
~🦶|foot|foot
~👂|ear|ear listen hear
~👃|nose|nose smell
🧠|brain|brain smart mind
🫀|anatomical heart|heart organ
🦴|bone|bone
👀|eyes|eyes look looking
👁️|eye|eye look
👅|tongue|tongue
👄|mouth|lips mouth
🫦|biting lip|nervous flirt
🦷|tooth|tooth dentist
~✍️|writing hand|write sign
~🤳|selfie|selfie photo
~💅|nail polish|nails manicure
~🙋|person raising hand|raise hand question me
~🤦|person facepalming|facepalm
~🤷|person shrugging|shrug whatever
~💁|person tipping hand|sassy information
~🙆|person gesturing OK|ok yes
~🙅|person gesturing NO|no stop nope
~🙇|person bowing|bow sorry apology
~💇|person getting haircut|haircut
~🧘|person in lotus position|yoga meditate calm
~🕺|man dancing|dance dancing
~💃|woman dancing|dance dancing
~🚶|person walking|walk walking
~🏃|person running|run running late
~🧍|person standing|stand standing
~🏊|person swimming|swim swimming
~🚴|person biking|bike cycling
~👶|baby|baby infant
~🧒|child|child kid
~🧑|person|person adult
~👩|woman|woman
~👨|man|man
~👵|old woman|grandma old
~👴|old man|grandpa old
~🤰|pregnant woman|pregnant
~👼|baby angel|angel
~🎅|Santa Claus|santa christmas
~🦸|superhero|superhero hero
~🕵️|detective|detective spy
~👮|police officer|police cop
~👷|construction worker|construction worker
👪|family|family
🫂|people hugging|hug hugging comfort
""";

    private const string Animals = """
🐶|dog face|dog puppy
🐱|cat face|cat kitten
🐭|mouse face|mouse
🐹|hamster|hamster
🐰|rabbit face|rabbit bunny
🦊|fox|fox
🐻|bear|bear
🐼|panda|panda
🐻‍❄️|polar bear|bear polar
🐨|koala|koala
🐯|tiger face|tiger
🦁|lion|lion
🐮|cow face|cow
🐷|pig face|pig
🐸|frog|frog
🐵|monkey face|monkey
🙈|see-no-evil monkey|monkey shy hide
🙉|hear-no-evil monkey|monkey
🙊|speak-no-evil monkey|monkey quiet oops
🐔|chicken|chicken
🐧|penguin|penguin
🐦|bird|bird
🐤|baby chick|chick
🦆|duck|duck
🦅|eagle|eagle
🦉|owl|owl
🦇|bat|bat
🐺|wolf|wolf
🐗|boar|boar pig
🐴|horse face|horse
🦄|unicorn|unicorn
🐝|honeybee|bee
🐛|bug|bug caterpillar
🦋|butterfly|butterfly
🐌|snail|snail slow
🐞|lady beetle|ladybug
🐜|ant|ant
🕷️|spider|spider
🦂|scorpion|scorpion
🐢|turtle|turtle slow
🐍|snake|snake
🦎|lizard|lizard gecko
🦖|T-Rex|dinosaur trex
🐙|octopus|octopus
🦑|squid|squid
🦐|shrimp|shrimp
🦀|crab|crab
🐡|blowfish|fish puffer
🐠|tropical fish|fish
🐟|fish|fish
🐬|dolphin|dolphin
🐳|spouting whale|whale
🐋|whale|whale
🦈|shark|shark
🐊|crocodile|crocodile alligator
🦓|zebra|zebra
🦍|gorilla|gorilla
🐘|elephant|elephant
🦒|giraffe|giraffe
🐪|camel|camel
🐑|ewe|sheep
🐐|goat|goat
🦌|deer|deer
🐕|dog|dog
🐈|cat|cat
🐇|rabbit|rabbit
🐿️|chipmunk|squirrel chipmunk
🦔|hedgehog|hedgehog
🐾|paw prints|paws pet
🌵|cactus|cactus
🎄|Christmas tree|christmas tree
🌲|evergreen tree|tree pine
🌳|deciduous tree|tree
🌴|palm tree|palm tree vacation
🌱|seedling|plant growing sprout
🌿|herb|plant herb
☘️|shamrock|clover
🍀|four leaf clover|luck clover
🍁|maple leaf|leaf fall autumn
🍂|fallen leaf|leaf fall autumn
🍃|leaf fluttering in wind|leaf wind
🍄|mushroom|mushroom
🌷|tulip|flower tulip
🌹|rose|flower rose
🥀|wilted flower|flower sad
🌺|hibiscus|flower
🌸|cherry blossom|flower blossom
🌼|blossom|flower daisy
🌻|sunflower|flower sun
🌞|sun with face|sun sunny
🌝|full moon face|moon
🌚|new moon face|moon
🌙|crescent moon|moon night
⭐|star|star
🌟|glowing star|star sparkle
✨|sparkles|sparkle shine magic
⚡|high voltage|lightning power
🔥|fire|fire hot flame
🌈|rainbow|rainbow pride
☀️|sun|sun sunny
⛅|sun behind cloud|cloud partly
☁️|cloud|cloud cloudy
🌧️|cloud with rain|rain rainy
⛈️|cloud with lightning and rain|storm thunderstorm
🌩️|cloud with lightning|lightning
🌪️|tornado|tornado
🌫️|fog|fog foggy
💨|dashing away|wind fast
❄️|snowflake|snow cold winter
☃️|snowman|snow winter
⛄|snowman without snow|snow
💧|droplet|water drop
🌊|water wave|wave ocean sea
🌎|globe showing Americas|earth world
🌍|globe showing Europe-Africa|earth world
🌏|globe showing Asia-Australia|earth world
""";

    private const string Food = """
🍏|green apple|apple
🍎|red apple|apple
🍐|pear|pear
🍊|tangerine|orange
🍋|lemon|lemon
🍌|banana|banana
🍉|watermelon|watermelon
🍇|grapes|grapes
🍓|strawberry|strawberry
🫐|blueberries|blueberry
🍈|melon|melon
🍒|cherries|cherry
🍑|peach|peach
🥭|mango|mango
🍍|pineapple|pineapple
🥥|coconut|coconut
🥝|kiwi fruit|kiwi
🍅|tomato|tomato
🍆|eggplant|eggplant aubergine
🥑|avocado|avocado
🥦|broccoli|broccoli
🥬|leafy green|lettuce greens
🥒|cucumber|cucumber pickle
🌶️|hot pepper|pepper spicy hot
🫑|bell pepper|pepper
🌽|ear of corn|corn
🥕|carrot|carrot
🧄|garlic|garlic
🧅|onion|onion
🥔|potato|potato
🍠|roasted sweet potato|potato sweet
🥐|croissant|croissant
🥯|bagel|bagel
🍞|bread|bread toast
🥖|baguette bread|bread baguette
🧀|cheese wedge|cheese
🥚|egg|egg
🍳|cooking|egg breakfast frying
🧇|waffle|waffle
🥞|pancakes|pancakes breakfast
🥓|bacon|bacon
🥩|cut of meat|steak meat
🍗|poultry leg|chicken drumstick
🍖|meat on bone|meat ribs
🌭|hot dog|hotdog
🍔|hamburger|burger
🍟|french fries|fries chips
🍕|pizza|pizza
🥪|sandwich|sandwich
🌮|taco|taco
🌯|burrito|burrito wrap
🥗|green salad|salad
🥘|shallow pan of food|food paella
🍝|spaghetti|pasta spaghetti
🍜|steaming bowl|ramen noodles soup
🍲|pot of food|stew soup
🍣|sushi|sushi
🍤|fried shrimp|shrimp tempura
🍙|rice ball|rice onigiri
🍚|cooked rice|rice
🥟|dumpling|dumpling potsticker
🍦|soft ice cream|ice cream
🍧|shaved ice|shaved ice
🍨|ice cream|ice cream
🍩|doughnut|donut
🍪|cookie|cookie biscuit
🎂|birthday cake|cake birthday
🍰|shortcake|cake slice
🧁|cupcake|cupcake
🥧|pie|pie
🍫|chocolate bar|chocolate
🍬|candy|candy sweet
🍭|lollipop|candy lollipop
🍯|honey pot|honey
🍼|baby bottle|milk baby
🥛|glass of milk|milk
☕|hot beverage|coffee tea
🫖|teapot|tea
🍵|teacup without handle|tea green
🧃|beverage box|juice
🥤|cup with straw|soda drink
🧋|bubble tea|boba tea
🍺|beer mug|beer
🍻|clinking beer mugs|beer cheers
🍷|wine glass|wine
🥂|clinking glasses|cheers celebrate toast
🍸|cocktail glass|cocktail martini
🍹|tropical drink|drink cocktail
🍾|bottle with popping cork|champagne celebrate
🥃|tumbler glass|whiskey
🧊|ice|ice cube
🍽️|fork and knife with plate|food dinner eat
🥢|chopsticks|chopsticks
🧂|salt|salt
""";

    private const string Activity = """
⚽|soccer ball|soccer football
🏀|basketball|basketball
🏈|american football|football
⚾|baseball|baseball
🥎|softball|softball
🎾|tennis|tennis
🏐|volleyball|volleyball
🏉|rugby football|rugby
🥏|flying disc|frisbee
🎱|pool 8 ball|pool billiards
🏓|ping pong|table tennis
🏸|badminton|badminton
🥊|boxing glove|boxing
🥋|martial arts uniform|karate judo
⛳|flag in hole|golf
⛸️|ice skate|skating
🎣|fishing pole|fishing
🤿|diving mask|diving snorkel
🎽|running shirt|running
🎿|skis|ski skiing
🛷|sled|sled sledding
🥌|curling stone|curling
🎯|bullseye|target darts goal
🪀|yo-yo|yoyo
🪁|kite|kite
🎮|video game|gaming game controller
🕹️|joystick|game arcade
🎲|game die|dice
🧩|puzzle piece|puzzle jigsaw
🎰|slot machine|casino gambling
🎳|bowling|bowling
🏆|trophy|trophy win champion
🥇|1st place medal|gold win first
🥈|2nd place medal|silver second
🥉|3rd place medal|bronze third
🏅|sports medal|medal
🎖️|military medal|medal
🎗️|reminder ribbon|ribbon awareness
🎫|ticket|ticket
🎟️|admission tickets|tickets
🎪|circus tent|circus
🎭|performing arts|theater drama
🎨|artist palette|art paint
🎬|clapper board|movie film action
🎤|microphone|mic sing karaoke
🎧|headphone|headphones music
🎼|musical score|music
🎵|musical note|music note
🎶|musical notes|music notes
🥁|drum|drums
🎷|saxophone|sax
🎺|trumpet|trumpet
🎸|guitar|guitar
🪕|banjo|banjo
🎻|violin|violin
🎹|musical keyboard|piano keyboard
🎉|party popper|party celebrate congrats
🎊|confetti ball|confetti party
🎈|balloon|balloon party
🎁|wrapped gift|gift present
🎀|ribbon|ribbon bow
🧨|firecracker|firework
🎇|sparkler|firework
🎆|fireworks|fireworks
🎃|jack-o-lantern|halloween pumpkin
""";

    private const string Travel = """
🚗|automobile|car
🚕|taxi|taxi cab
🚙|sport utility vehicle|suv car
🚌|bus|bus
🏎️|racing car|race car fast
🚓|police car|police
🚑|ambulance|ambulance
🚒|fire engine|fire truck
🚐|minibus|van
🛻|pickup truck|truck pickup
🚚|delivery truck|truck delivery
🚛|articulated lorry|truck semi
🚜|tractor|tractor farm
🛴|kick scooter|scooter
🚲|bicycle|bike bicycle
🛵|motor scooter|scooter moped
🏍️|motorcycle|motorcycle
🚨|police car light|siren alert emergency
🚅|bullet train|train
🚆|train|train
🚇|metro|subway metro
🚊|tram|tram
🚉|station|train station
✈️|airplane|plane flight fly
🛫|airplane departure|takeoff departure
🛬|airplane arrival|landing arrival
🪂|parachute|parachute skydive
💺|seat|seat
🚁|helicopter|helicopter
🚀|rocket|rocket launch space
🛸|flying saucer|ufo alien
🚢|ship|ship cruise
⛵|sailboat|sailboat sailing
🛥️|motor boat|boat
🛶|canoe|canoe kayak
⚓|anchor|anchor
🗺️|world map|map
🧭|compass|compass direction
🏔️|snow-capped mountain|mountain
⛰️|mountain|mountain
🌋|volcano|volcano
🏕️|camping|camp camping
🏖️|beach with umbrella|beach vacation
🏝️|desert island|island vacation
🏜️|desert|desert
🏞️|national park|park nature
🏟️|stadium|stadium arena
🏠|house|house home
🏡|house with garden|house home
🏢|office building|office work
🏥|hospital|hospital
🏦|bank|bank
🏨|hotel|hotel
🏫|school|school
🏭|factory|factory
🏰|castle|castle
🗽|Statue of Liberty|liberty statue
🗼|Tokyo Tower|tower
⛲|fountain|fountain
🎡|ferris wheel|fair carnival
🎢|roller coaster|coaster
🎠|carousel horse|carousel
⛺|tent|tent camping
🌃|night with stars|night
🌆|cityscape at dusk|city dusk
🌇|sunset|sunset
🌉|bridge at night|bridge
🌌|milky way|stars space galaxy
🏙️|cityscape|city skyline
🗿|moai|statue
🚏|bus stop|bus stop
🛣️|motorway|highway road
🚧|construction|construction roadwork
""";

    private const string Objects = """
⌚|watch|watch time
📱|mobile phone|phone cell
📲|mobile phone with arrow|phone call
💻|laptop|laptop computer
🖥️|desktop computer|computer pc
⌨️|keyboard|keyboard typing
🖨️|printer|printer print
🖱️|computer mouse|mouse
💾|floppy disk|save disk
💿|optical disk|cd disc
📷|camera|camera photo
📸|camera with flash|camera photo
📹|video camera|video record
🎥|movie camera|film movie
📺|television|tv
📻|radio|radio
🔋|battery|battery charge
🔌|electric plug|plug power
💡|light bulb|idea light
🔦|flashlight|flashlight torch
🕯️|candle|candle
🧯|fire extinguisher|extinguisher
🛒|shopping cart|cart shopping
💰|money bag|money cash
💵|dollar banknote|money dollar cash
💳|credit card|card payment
🧾|receipt|receipt bill
💎|gem stone|diamond gem
⚖️|balance scale|scales justice
🔧|wrench|wrench tool fix
🔨|hammer|hammer tool
🛠️|hammer and wrench|tools repair
🪛|screwdriver|screwdriver tool
🔩|nut and bolt|bolt hardware
⚙️|gear|gear settings
🧰|toolbox|tools toolbox
🧲|magnet|magnet
🪜|ladder|ladder
🧱|brick|brick wall
🔑|key|key
🗝️|old key|key
🔒|locked|lock secure private
🔓|unlocked|unlock open
🔗|link|link chain url
⛓️|chains|chains
🧷|safety pin|pin
🧵|thread|thread sewing
🧶|yarn|yarn knitting
👓|glasses|glasses
🕶️|sunglasses|sunglasses
🥼|lab coat|lab coat
🦺|safety vest|vest safety
👔|necktie|tie
👕|t-shirt|shirt tshirt
👖|jeans|jeans pants
🧣|scarf|scarf
🧤|gloves|gloves
🧥|coat|coat jacket
🧦|socks|socks
👗|dress|dress
🩳|shorts|shorts
👙|bikini|bikini swimsuit
👜|handbag|bag purse
🎒|backpack|backpack school
👟|running shoe|sneaker shoe
🥾|hiking boot|boot hiking
👠|high-heeled shoe|heels shoe
👑|crown|crown king queen
🎩|top hat|hat
🧢|billed cap|cap hat
💍|ring|ring engagement wedding
💼|briefcase|work business
📁|file folder|folder file
📂|open file folder|folder open
📅|calendar|calendar date
📆|tear-off calendar|calendar
📋|clipboard|clipboard list
📌|pushpin|pin
📍|round pushpin|pin location
📎|paperclip|clip attach
📏|straight ruler|ruler
📐|triangular ruler|ruler
✂️|scissors|scissors cut
🗑️|wastebasket|trash delete bin
📦|package|package box shipping
📫|closed mailbox with raised flag|mail mailbox
✉️|envelope|mail email letter
📧|e-mail|email
📨|incoming envelope|mail
📤|outbox tray|send outbox
📥|inbox tray|inbox
📜|scroll|scroll document
📄|page facing up|document page file
📊|bar chart|chart graph data
📈|chart increasing|chart up growth
📉|chart decreasing|chart down loss
📚|books|books library
📖|open book|book read reading
📓|notebook|notebook
📝|memo|note write notes
✏️|pencil|pencil write edit
✒️|black nib|pen
🖊️|pen|pen
🖌️|paintbrush|brush paint
🖍️|crayon|crayon
🔍|magnifying glass tilted left|search zoom find
🔎|magnifying glass tilted right|search find
🔬|microscope|microscope science
🔭|telescope|telescope
🧪|test tube|science chemistry
🧬|dna|dna genetics
💊|pill|pill medicine
💉|syringe|shot vaccine needle
🩹|adhesive bandage|bandage
🩺|stethoscope|doctor health
🚪|door|door
🪑|chair|chair seat
🛏️|bed|bed sleep
🛋️|couch and lamp|couch sofa
🚽|toilet|toilet bathroom
🚿|shower|shower
🛁|bathtub|bath bathtub
🧹|broom|broom clean sweep
🧺|basket|basket laundry
🧼|soap|soap wash
🪥|toothbrush|toothbrush
🔔|bell|bell notification alert
🔕|bell with slash|mute silent
📣|megaphone|announce cheer
📢|loudspeaker|announce loud
🔊|speaker high volume|volume sound loud
🔇|muted speaker|mute silent
⏰|alarm clock|alarm clock wake
⏱️|stopwatch|stopwatch timer
⌛|hourglass done|time hourglass
⏳|hourglass not done|time waiting
🧸|teddy bear|teddy toy
🪙|coin|coin money
🧿|nazar amulet|evil eye
🔮|crystal ball|fortune magic
""";

    private const string Symbols = """
❤️|red heart|love heart
🧡|orange heart|heart orange
💛|yellow heart|heart yellow
💚|green heart|heart green
💙|blue heart|heart blue
💜|purple heart|heart purple
🖤|black heart|heart black
🤍|white heart|heart white
🤎|brown heart|heart brown
💔|broken heart|heartbreak sad breakup
❤️‍🔥|heart on fire|love passion
❤️‍🩹|mending heart|healing
💕|two hearts|love hearts
💞|revolving hearts|love hearts
💓|beating heart|love heartbeat
💗|growing heart|love
💖|sparkling heart|love sparkle
💘|heart with arrow|love cupid valentine
💝|heart with ribbon|love gift
💟|heart decoration|heart
💋|kiss mark|kiss lips lipstick
💯|hundred points|100 perfect score
💢|anger symbol|anger mad
💥|collision|boom explode bang
💫|dizzy|dizzy stars
💦|sweat droplets|sweat water splash
🕳️|hole|hole
💬|speech balloon|chat message comment
💭|thought balloon|thought thinking
🗯️|right anger bubble|angry shouting
💤|zzz|sleep snore
✅|check mark button|yes done check
☑️|check box with check|check done
✔️|check mark|check yes
❌|cross mark|no wrong incorrect
❎|cross mark button|no
⭕|hollow red circle|circle correct
❗|exclamation mark|important warning
❓|question mark|question help
❕|white exclamation mark|exclamation
❔|white question mark|question
‼️|double exclamation mark|important
⁉️|exclamation question mark|what confused
⚠️|warning|warning caution careful
🚫|prohibited|no forbidden banned
🔞|no one under eighteen|adult
♻️|recycling symbol|recycle
✳️|eight-spoked asterisk|asterisk
❇️|sparkle|sparkle
🔱|trident emblem|trident
⚜️|fleur-de-lis|fleur
⚕️|medical symbol|medical health
♾️|infinity|infinity forever
🆗|OK button|ok
🆕|NEW button|new
🆓|FREE button|free
🔝|TOP arrow|top up
🔙|BACK arrow|back
🔜|SOON arrow|soon
🔛|ON arrow|on
🔚|END arrow|end
🔄|counterclockwise arrows button|refresh reload sync
🔁|repeat button|repeat loop
🔂|repeat single button|repeat one
▶️|play button|play start
⏸️|pause button|pause
⏹️|stop button|stop
⏺️|record button|record
⏭️|next track button|next skip
⏮️|last track button|previous back
⏩|fast-forward button|forward fast
⏪|fast reverse button|rewind back
🔼|upwards button|up
🔽|downwards button|down
⬆️|up arrow|up arrow
⬇️|down arrow|down arrow
⬅️|left arrow|left arrow
➡️|right arrow|right arrow
↗️|up-right arrow|arrow diagonal
↘️|down-right arrow|arrow diagonal
↕️|up-down arrow|arrow vertical
↔️|left-right arrow|arrow horizontal
↩️|right arrow curving left|reply back return
↪️|left arrow curving right|forward
🔀|shuffle tracks button|shuffle random
➕|plus|plus add more
➖|minus|minus subtract less
➗|divide|divide
✖️|multiply|multiply times
💲|heavy dollar sign|dollar money
#️⃣|keycap hash|hash number
0️⃣|keycap 0|zero number
1️⃣|keycap 1|one number
2️⃣|keycap 2|two number
3️⃣|keycap 3|three number
4️⃣|keycap 4|four number
5️⃣|keycap 5|five number
6️⃣|keycap 6|six number
7️⃣|keycap 7|seven number
8️⃣|keycap 8|eight number
9️⃣|keycap 9|nine number
🔟|keycap 10|ten number
⚫|black circle|circle black
⚪|white circle|circle white
🔴|red circle|circle red
🟠|orange circle|circle orange
🟡|yellow circle|circle yellow
🟢|green circle|circle green
🔵|blue circle|circle blue
🟣|purple circle|circle purple
🟤|brown circle|circle brown
🟥|red square|square red
🟧|orange square|square orange
🟨|yellow square|square yellow
🟩|green square|square green
🟦|blue square|square blue
🟪|purple square|square purple
⬛|black large square|square black
⬜|white large square|square white
🔶|large orange diamond|diamond orange
🔷|large blue diamond|diamond blue
🔺|red triangle pointed up|triangle up
🔻|red triangle pointed down|triangle down
♠️|spade suit|cards spade
♥️|heart suit|cards heart
♦️|diamond suit|cards diamond
♣️|club suit|cards club
🃏|joker|cards joker
♈|Aries|zodiac aries
♉|Taurus|zodiac taurus
♊|Gemini|zodiac gemini
♋|Cancer|zodiac cancer
♌|Leo|zodiac leo
♍|Virgo|zodiac virgo
♎|Libra|zodiac libra
♏|Scorpio|zodiac scorpio
♐|Sagittarius|zodiac sagittarius
♑|Capricorn|zodiac capricorn
♒|Aquarius|zodiac aquarius
♓|Pisces|zodiac pisces
""";
}
