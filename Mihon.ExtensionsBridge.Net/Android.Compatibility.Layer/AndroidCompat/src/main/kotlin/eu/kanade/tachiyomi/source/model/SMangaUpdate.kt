package eu.kanade.tachiyomi.source.model

/**
 * extensions-lib 1.6: the result of the combined details+chapters call [getMangaUpdate].
 * Newer sources (e.g. GraphQL-backed ones like AllManga) implement getMangaUpdate and leave the
 * individual getMangaDetails/getChapterList throwing, so callers must go through this.
 */
class SMangaUpdate(
    val manga: SManga,
    val chapters: List<SChapter>,
)
