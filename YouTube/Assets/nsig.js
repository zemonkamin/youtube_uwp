/*
 * Решатель задачи `n` из скрипта плеера. Только ES5: исполняется в веб-виде
 * Android 4.1 и в UIWebView iOS 5.
 *
 * Скрипт плеера не разбирается — он исполняется целиком, а нужная функция
 * находится по поведению. Текстом ищется только одно: функция, которая
 * ставит параметр `alr=yes`. Она берёт адрес и возвращает объект адреса,
 * у которого есть метод, применяющий преобразование `n`. Какой из методов
 * этим занимается, какой читает параметр, какой ставит — выясняется опытом
 * на пробном значении, а не по именам: имена меняются с каждой сборкой.
 */
var YTSolver = (function () {
    var TEST_URL = "https://www.youtube.com/watch?v=yt-dlp-wins";
    var SAMPLE = "DhpWuaCJRFiGbHK";
    var MARKER = '("alr","yes")';

    var notes = [];
    var found = null;

    function note(line) {
        notes.push(line);
    }

    function globalObject() {
        return Function("return this")();
    }

    /**
     * Имена функций, внутри которых стоит вызов `…("alr","yes")`.
     *
     * От каждого вхождения идём назад и берём несколько ближайших
     * определений вида `имя=function(` или `function имя(`. Лишние имена
     * не страшны: каждое потом проверяется делом, и негодное отпадает.
     */
    function candidates(text) {
        var names = [];
        var seen = {};
        var at = -1;

        while ((at = text.indexOf(MARKER, at + 1)) >= 0) {
            var from = at > 20000 ? at - 20000 : 0;
            var span = text.substring(from, at);
            var re = /(?:^|[\n;{}])(?:var\s+)?([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)\s*=\s*function\s*\(|(?:^|[\n;{}])function\s+([A-Za-z_$][\w$]*)\s*\(/g;
            var near = [];
            var m;

            while ((m = re.exec(span))) {
                near.push(m[1] || m[2]);
            }

            for (var i = near.length - 1; i >= 0 && i >= near.length - 4; i--) {
                if (!seen[near[i]]) {
                    seen[near[i]] = true;
                    names.push(near[i]);
                }
            }
        }

        return names;
    }

    /**
     * Вставляет в начало тела плеера крючок, который отдаёт наружу
     * функции по найденным именам. Внутри замыкания они видны, снаружи —
     * нет, поэтому иначе до них не добраться.
     */
    function inject(text, names) {
        var start = text.indexOf("(function(");

        if (start < 0 || start > 65536) {
            return null;
        }

        var open = text.indexOf("{", start);

        if (open < 0) {
            return null;
        }

        var parts = [];

        for (var i = 0; i < names.length; i++) {
            parts.push("try{r.push(" + names[i] + ");}catch(e){}");
        }

        var hook = 'Function("return this")().__ytCands=function(){var r=[];' +
            parts.join("") + "return r;};";

        return text.substring(0, open + 1) + hook + text.substring(open + 1);
    }

    /** Имена методов объекта, включая унаследованные от его прототипа. */
    function methodsOf(obj) {
        var names = [];
        var seen = {};
        var o = obj;
        var depth = 0;

        while (o && o !== Object.prototype && depth < 3) {
            var keys = Object.getOwnPropertyNames(o);

            for (var i = 0; i < keys.length; i++) {
                var k = keys[i];

                if (seen[k] || k === "constructor") {
                    continue;
                }

                seen[k] = true;

                try {
                    if (typeof obj[k] === "function") {
                        names.push(k);
                    }
                } catch (e) {}
            }

            o = Object.getPrototypeOf(o);
            depth++;
        }

        return names;
    }

    function fresh(f, sig) {
        return f(TEST_URL, "s", sig);
    }

    /**
     * Один проход преобразования: свежий объект, запись `n`, при
     * необходимости отдельный метод, чтение `n`. Порядок важен: объект
     * применяет преобразование один раз, при первом же обращении к нему
     * после записи, — обращаться к нему до записи нельзя.
     */
    function pass(f, set, fix, get, value) {
        var u = fresh(f);

        u[set]("n", value);

        if (fix) {
            u[fix]();
        }

        return u[get]("n");
    }

    /**
     * Проверка кандидата делом. Годный возвращает объект адреса, у которого
     * находятся: чтение параметра (отдаёт `yes` для `alr`), запись
     * (записанное под посторонним именем читается обратно) и
     * преобразование — после записи `n` читается другим, причём одинаково
     * от раза к разу. Если само чтение его не запускает, перебираются
     * остальные методы без доводов.
     */
    function probe(f, name) {
        var u;

        try {
            u = fresh(f);
        } catch (e) {
            note(name + ": вызов упал: " + e);
            return null;
        }

        if (!u || typeof u !== "object") {
            note(name + ": вернул не объект");
            return null;
        }

        var ms = methodsOf(u);
        var get = null;
        var set = null;
        var order = ["get"].concat(ms);
        var i, k;

        for (i = 0; i < order.length && !get; i++) {
            k = order[i];

            try {
                if (typeof u[k] === "function" && u[k]("alr") === "yes") {
                    get = k;
                }
            } catch (e) {}
        }

        if (!get) {
            note(name + ": нет чтения параметра (методы: " + ms.join(",") + ")");
            return null;
        }

        order = ["set"].concat(ms);

        for (i = 0; i < order.length && !set; i++) {
            k = order[i];

            if (k === get) {
                continue;
            }

            try {
                var v = fresh(f);

                if (typeof v[k] === "function") {
                    v[k]("zz", "ytdlp");

                    if (v[get]("zz") === "ytdlp") {
                        set = k;
                    }
                }
            } catch (e) {}
        }

        if (!set) {
            note(name + ": нет записи параметра");
            return null;
        }

        var fixes = [null];

        for (i = 0; i < ms.length; i++) {
            if (ms[i] !== get && ms[i] !== set) {
                fixes.push(ms[i]);
            }
        }

        for (i = 0; i < fixes.length; i++) {
            k = fixes[i];

            try {
                var out = pass(f, set, k, get, SAMPLE);

                if (typeof out === "string" && out.length > 0 && out !== SAMPLE &&
                    pass(f, set, k, get, SAMPLE) === out) {
                    return { name: name, get: get, set: set, fix: k, fn: f, sample: out };
                }
            } catch (e) {}
        }

        note(name + ": нет преобразования (методы: " + ms.join(",") + ")");

        return null;
    }

    function install(s) {
        var g = globalObject();

        g.YTn = function (n) {
            try {
                var r = pass(s.fn, s.set, s.fix, s.get, n);

                return typeof r === "string" ? r : "";
            } catch (e) {
                return "";
            }
        };

        g.YTsig = function (sig) {
            try {
                var u = fresh(s.fn, encodeURIComponent(sig));
                var r = u[s.get]("s");

                return r ? decodeURIComponent(r) : "";
            } catch (e) {
                return "";
            }
        };
    }

    /**
     * Принимает текст скрипта плеера, исполняет его и заводит `YTn`.
     * Возвращает строку отчёта: `ok …` либо причину неудачи.
     */
    /** Отметка этапа — в консоль страницы; снаружи её видно в журнале. */
    function stage(line) {
        try {
            console.log("nsig: " + line);
        } catch (e) {}
    }

    function boot(text) {
        notes = [];
        found = null;

        stage("скрипт " + text.length + " знаков");

        var names = candidates(text);

        if (!names.length) {
            return "нет метки " + MARKER;
        }

        stage("кандидаты: " + names.join(","));

        var code = inject(text, names);

        if (!code) {
            return "не нашлось начало тела плеера";
        }

        var g = globalObject();

        g.__ytCands = null;

        try {
            Function(code)();
        } catch (e) {
            note("скрипт плеера упал: " + e);
        }

        stage("скрипт плеера исполнен" + (notes.length ? " (" + notes.join("; ") + ")" : ""));

        var fns = [];

        try {
            fns = g.__ytCands ? g.__ytCands() : [];
        } catch (e) {}

        if (!fns.length) {
            return "имена не в области видимости: " + names.join(",") + "; " + notes.join("; ");
        }

        for (var i = 0; i < fns.length; i++) {
            var s = probe(fns[i], names[i] || ("#" + i));

            if (s) {
                found = s;
                install(s);

                return "ok " + s.name + " " + s.set + "/" + s.get +
                    (s.fix ? "/" + s.fix : "") + " " + SAMPLE + "->" + s.sample;
            }
        }

        return "кандидаты не прошли проверку: " + notes.join("; ");
    }

    function bootBase64(b64) {
        var text;

        stage("base64 " + (b64 ? b64.length : 0) + " знаков");

        try {
            text = atob(b64);
        } catch (e) {
            return "не раскодировался base64: " + e;
        }

        return boot(text);
    }

    return { boot: boot, bootBase64: bootBase64, candidates: candidates };
})();
