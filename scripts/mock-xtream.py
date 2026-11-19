"""A fake Xtream Codes panel for testing Veltrio's IPTV screens.

Serves player_api.php (account, live/VOD/series lists, series info, short
EPG, catch-up table), xmltv.php (gzipped XMLTV), and redirects stream URLs
(/live, /movie, /series, /timeshift) to public test media.
Usage: python3 scripts/mock-xtream.py 10.211.55.2 8770  (then add an Xtream source
in the app: server 10.211.55.2:8770, user "test", password "pass")
"""
import base64, gzip, http.server, json, sys, time, urllib.parse
from datetime import datetime, timedelta, timezone

USER, PASS = "test", "pass"
LIVE = [
    ("101", "20 Minutes TV", "1", "20minutes.fr", "https://upload.wikimedia.org/wikipedia/commons/thumb/c/cb/20_Minutes_TV_IDF_logo_%282023%29.png/960px-20_Minutes_TV_IDF_logo_%282023%29.png", "https://live-20minutestv.digiteka.com/1961167769/index.m3u8", 1),
    ("102", "BFM TV", "2", "bfmtv.fr", "https://i.imgur.com/YJCcczD.png", "https://n-5-2.eu.cdn-redge.media/livehls/oeu/mt/live/bfm_tv/live.livx/playlist.m3u8", 0),
    ("103", "LCI", "2", "lci.fr", "https://i.imgur.com/DED8EXs.png", "https://r.eu.cdn-redge.media/livehls/oeu/mt/live/lci/live.livx/playlist.m3u8", 1),
    ("104", "Euronews", "2", "", "https://i.imgur.com/8t9mdg9.png", "https://2f6c5bf4.wurl.com/master/f36d25e7e52f1ba8d7e56eb859c636563214f541/UmxheHhUVi1ldV9FdXJvbmV3c0ZyYW5jYWlzX0hMUw/playlist.m3u8", 0),
]
H264 = "https://repo.jellyfin.org/archive/jellyfish/media/jellyfish-10-mbps-hd-h264.mkv"
HEVC = "https://repo.jellyfin.org/archive/jellyfish/media/jellyfish-10-mbps-hd-hevc.mkv"
TRACKS = "https://github.com/ietf-wg-cellar/matroska-test-files/raw/master/test_files/test5.mkv"
VOD = [("201", "Méduses (H.264)", "10", H264, "2019", "7.4"), ("202", "Méduses (HEVC)", "10", HEVC, "2020", "8.1"),
       ("203", "Big Buck Bunny – pistes", "11", TRACKS, "2008", "6.9")]
PROGRAMMES = ["Le journal", "La matinale", "Météo", "Débat du jour", "Reportage", "Le grand entretien", "Sport", "Documentaire"]

def slots(channel_index):
    """Half-hour programmes from 4 h back to 12 h ahead, aligned on :00/:30."""
    now = datetime.now(timezone.utc).replace(second=0, microsecond=0)
    start = now.replace(minute=0 if now.minute < 30 else 30) - timedelta(hours=4)
    out = []
    for i in range(32):
        s = start + timedelta(minutes=30 * i)
        title = f"{PROGRAMMES[(i + channel_index) % len(PROGRAMMES)]}"
        out.append((s, s + timedelta(minutes=30), title, f"{title} : description de test numéro {i}."))
    return out

def b64(s): return base64.b64encode(s.encode()).decode()

def epg_rows(stream_id, past_only=False):
    idx = [l[0] for l in LIVE].index(stream_id)
    now = datetime.now(timezone.utc)
    rows = []
    for s, e, t, d in slots(idx):
        if past_only and e > now: continue
        if not past_only and e <= now: continue
        rows.append({"id": f"{stream_id}{int(s.timestamp())}", "epg_id": "1", "title": b64(t), "lang": "fr",
                     "start": s.strftime("%Y-%m-%d %H:%M:%S"), "end": e.strftime("%Y-%m-%d %H:%M:%S"),
                     "description": b64(d), "channel_id": stream_id,
                     "start_timestamp": str(int(s.timestamp())), "stop_timestamp": str(int(e.timestamp())),
                     "has_archive": 1 if past_only else 0})
    return rows[:6] if not past_only else rows

def xmltv():
    lines = ['<?xml version="1.0" encoding="UTF-8"?>', '<tv generator-info-name="mock">']
    for i, (sid, name, cat, epg, logo, url, arch) in enumerate(LIVE):
        cid = epg or "euronews.fr"
        lines.append(f'<channel id="{cid}"><display-name>{name}</display-name></channel>')
    for i, (sid, name, cat, epg, logo, url, arch) in enumerate(LIVE):
        cid = epg or "euronews.fr"
        for s, e, t, d in slots(i):
            lines.append(f'<programme start="{s.strftime("%Y%m%d%H%M%S")} +0000" stop="{e.strftime("%Y%m%d%H%M%S")} +0000" channel="{cid}">'
                         f'<title lang="fr">{t}</title><desc lang="fr">{d}</desc><category>Info</category></programme>')
    lines.append('</tv>')
    return gzip.compress("\n".join(lines).encode())

class H(http.server.BaseHTTPRequestHandler):
    def send(self, code, body=b"", ctype="application/json", headers=None):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        for k, v in (headers or {}).items(): self.send_header(k, v)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def json(self, obj): self.send(200, json.dumps(obj).encode())

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = dict(urllib.parse.parse_qsl(u.query))
        p = u.path
        if p == "/player_api.php":
            if q.get("username") != USER or q.get("password") != PASS:
                return self.json({"user_info": {"auth": 0}})
            a = q.get("action")
            if a is None:
                return self.json({"user_info": {"auth": 1, "status": "Active", "username": USER, "exp_date": str(int(time.time()) + 86400 * 30)},
                                  "server_info": {"url": "mock"}})
            if a == "get_live_categories": return self.json([{"category_id": "1", "category_name": "Généralistes"}, {"category_id": "2", "category_name": "Infos"}])
            if a == "get_live_streams":
                return self.json([{"num": i + 1, "name": n, "stream_id": int(s), "stream_icon": logo, "epg_channel_id": epg or None,
                                   "category_id": c, "tv_archive": arch, "tv_archive_duration": 3 if arch else 0}
                                  for i, (s, n, c, epg, logo, url, arch) in enumerate(LIVE)])
            if a == "get_vod_categories": return self.json([{"category_id": "10", "category_name": "Nature"}, {"category_id": "11", "category_name": "Animation"}])
            if a == "get_vod_streams":
                return self.json([{"name": n, "stream_id": s, "stream_icon": "https://image.tmdb.org/t/p/w300/uXDfjJbdP4ijW5hWSBrPrlKpxab.jpg",
                                   "category_id": c, "container_extension": "mkv", "year": y, "rating": r} for s, n, c, url, y, r in VOD])
            if a == "get_series_categories": return self.json([{"category_id": "20", "category_name": "Séries test"}])
            if a == "get_series":
                return self.json([{"series_id": 301, "name": "Les Méduses", "cover": "https://image.tmdb.org/t/p/w300/uXDfjJbdP4ijW5hWSBrPrlKpxab.jpg",
                                   "category_id": "20", "rating": "7.9", "releaseDate": "2021-05-01"}])
            if a == "get_series_info":
                eps = {"1": [{"id": "3011", "episode_num": 1, "title": "Le grand bleu", "container_extension": "mkv", "season": 1, "info": {"plot": "Premier épisode.", "duration": "00:00:30"}},
                             {"id": "3012", "episode_num": 2, "title": "En HEVC", "container_extension": "mkv", "season": 1, "info": {"plot": "Deuxième épisode.", "duration": "00:00:30"}}],
                       "2": [{"id": "3021", "episode_num": 1, "title": "Pistes multiples", "container_extension": "mkv", "season": 2, "info": {"plot": "Avec sous-titres.", "duration": "00:00:46"}}]}
                return self.json({"info": {"name": "Les Méduses", "plot": "Une série de test servie par le faux panneau Xtream.", "genre": "Nature",
                                           "releaseDate": "2021-05-01", "rating": "7.9", "cover": "https://image.tmdb.org/t/p/w300/uXDfjJbdP4ijW5hWSBrPrlKpxab.jpg"},
                                  "episodes": eps})
            if a == "get_short_epg": return self.json({"epg_listings": epg_rows(q.get("stream_id", "101"))})
            if a == "get_simple_data_table": return self.json({"epg_listings": epg_rows(q.get("stream_id", "101"), past_only=True)})
            return self.json([])
        if p == "/xmltv.php":
            return self.send(200, xmltv(), "application/gzip")
        parts = p.strip("/").split("/")
        if parts[0] == "live" and len(parts) == 4:
            sid = parts[3].split(".")[0]
            for s, n, c, epg, logo, url, arch in LIVE:
                if s == sid: return self.send(302, headers={"Location": url})
        if parts[0] == "movie" and len(parts) == 4:
            sid = parts[3].split(".")[0]
            for s, n, c, url, y, r in VOD:
                if s == sid: return self.send(302, headers={"Location": url})
        if parts[0] == "series" and len(parts) == 4:
            eid = parts[3].split(".")[0]
            return self.send(302, headers={"Location": {"3011": H264, "3012": HEVC}.get(eid, TRACKS)})
        if parts[0] == "timeshift":
            return self.send(302, headers={"Location": H264})
        self.send(404, b"not found", "text/plain")

    def log_message(self, fmt, *args):
        sys.stderr.write("%s\n" % (fmt % args))

http.server.ThreadingHTTPServer((sys.argv[1], int(sys.argv[2])), H).serve_forever()
