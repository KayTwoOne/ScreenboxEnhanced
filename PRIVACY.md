# Screenbox Enhanced - Privacy Policy

Screenbox Enhanced is a fork of [Screenbox](https://github.com/huynhsontung/Screenbox) by Tung Huynh. This policy covers builds published from this repository.

## Your media stays on your device
Your library, watch history, playback positions, custom titles and custom posters are stored locally on your device. None of it is uploaded anywhere.

## Telemetry
Screenbox Enhanced can report crashes and errors to [Sentry](https://sentry.io/), but only when a build is configured with a Sentry DSN. Builds published from this repository ship without one, so they send no crash reports.

If a build is configured with a DSN, a report contains exception and diagnostic details such as log messages, exception messages and stack traces, plus your machine model name, Windows version and Windows language. It does not contain personally identifiable information, and Screenbox Enhanced never collects usage behaviour or tracks in-app activity.

## Optional online metadata
Screenbox Enhanced can look up series titles, synopses, release years and poster art online. **This is off by default, and the app makes no request to any metadata service until you turn on Settings > Libraries > Online metadata.**

When it is on, a lookup works as follows:

- **What is sent:** a search term built from a folder's name, cleaned of things like "Season 2". For a bare season folder such as `Season 1`, the parent folder's name is used instead. File names, full folder paths, file contents and watch history are never sent.
- **Who receives it:** only the services you enable:
  - [AniList](https://anilist.co) (`graphql.anilist.co`)
  - [Jikan](https://jikan.moe), an unofficial MyAnimeList API (`api.jikan.moe`)
  - [TMDB](https://www.themoviedb.org) (`api.themoviedb.org`), which needs your own API key
- **Poster images** are downloaded over HTTPS from the address the service returns, and saved locally.
- **Your TMDB API key** is stored in the app's local settings on your device. It is sent only to TMDB, and it is never logged.

Each service's own privacy policy governs what it does with the requests it receives. You can turn the feature off at any time. Already-downloaded posters and titles stay on your device until you change or remove them.

This product uses the TMDB API but is not endorsed or certified by TMDB.
