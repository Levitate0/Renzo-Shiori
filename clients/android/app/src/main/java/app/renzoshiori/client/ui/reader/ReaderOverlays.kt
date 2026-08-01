package app.renzoshiori.client.ui.reader

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ChevronLeft
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.OpenInNew
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.ui.theme.RenzoColors

/** "FINISHED" / "UP NEXT" eyebrow — web: text-[11px] uppercase tracking-[0.15em] text-white/35. */
@Composable
private fun Eyebrow(text: String) {
    Text(
        text.uppercase(),
        fontSize = 11.sp,
        letterSpacing = 1.6.sp,
        color = ReaderPalette.Text35,
        textAlign = TextAlign.Center,
    )
}

@Composable
private fun SecondaryButton(text: String, onClick: () -> Unit, content: (@Composable () -> Unit)? = null) {
    Button(
        onClick = onClick,
        shape = MaterialTheme.shapes.small,
        colors = ButtonDefaults.buttonColors(
            containerColor = RenzoColors.Secondary,
            contentColor = ReaderPalette.Text,
        ),
    ) {
        content?.invoke()
        Text(text, fontSize = 14.sp)
    }
}

/**
 * Footer of a continuous chapter. With infinite scroll on, the next chapter is
 * appended before this is reached, so it mostly surfaces at the end of a series
 * or when appending failed. It fills the screen deliberately: a late-shrinking
 * last-page image needs guaranteed scroll distance below it for the page
 * tracker to register the final page and mark the chapter read.
 */
@Composable
fun EndOfChapter(
    chapterLabel: String,
    nextLabel: String?,
    hasNext: Boolean,
    hasPrev: Boolean,
    infinite: Boolean,
    onNext: () -> Unit,
    onPrev: () -> Unit,
    onExit: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.fillMaxWidth().padding(24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Eyebrow("Finished")
        Text(
            chapterLabel,
            fontSize = 14.sp,
            fontWeight = FontWeight.Medium,
            color = ReaderPalette.Text80,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 4.dp),
        )
        if (hasNext && nextLabel != null) {
            Spacer(Modifier.height(16.dp))
            Eyebrow("Up next")
            Text(
                nextLabel,
                fontSize = 14.sp,
                fontWeight = FontWeight.Medium,
                color = RenzoColors.Primary,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(top = 4.dp),
            )
        }
        Spacer(Modifier.height(16.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            if (hasPrev) {
                SecondaryButton("Previous", onPrev) {
                    Icon(Icons.Filled.ChevronLeft, contentDescription = null, modifier = Modifier.size(16.dp))
                }
            }
            if (hasNext) {
                Button(onClick = onNext, shape = MaterialTheme.shapes.small) {
                    Text("Next chapter", fontSize = 14.sp)
                    Icon(Icons.Filled.ChevronRight, contentDescription = null, modifier = Modifier.size(16.dp))
                }
            } else {
                SecondaryButton("Back to series", onExit)
            }
        }
        if (hasNext) {
            Text(
                if (infinite) "keep scrolling to continue" else "or tap the right side",
                fontSize = 12.sp,
                color = ReaderPalette.Text35,
                modifier = Modifier.padding(top = 12.dp),
            )
        }
    }
}

/**
 * Paged-mode chapter transition — its own "page" between two chapters. Taps flow
 * through to the reader's page-turn zones (one tap forward enters the next
 * chapter); the buttons are the explicit way to jump.
 */
@Composable
fun ChapterTransition(
    finishedLabel: String,
    nextLabel: String?,
    hasNext: Boolean,
    hasPrev: Boolean,
    onNext: () -> Unit,
    onPrevPage: () -> Unit,
    onPrevChapter: () -> Unit,
    onExit: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Eyebrow("Finished")
        Text(
            finishedLabel,
            fontSize = 16.sp,
            fontWeight = FontWeight.Medium,
            color = ReaderPalette.Text85,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 4.dp),
        )
        Spacer(Modifier.height(24.dp))
        if (hasNext && nextLabel != null) {
            Eyebrow("Up next")
            Text(
                nextLabel,
                fontSize = 16.sp,
                fontWeight = FontWeight.Medium,
                color = RenzoColors.Primary,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(top = 4.dp),
            )
        } else {
            Text("You're all caught up.", fontSize = 14.sp, color = ReaderPalette.Text50)
        }
        Spacer(Modifier.height(24.dp))
        // Web puts these in one wrapping row; on a phone they stack vertically.
        Column(
            verticalArrangement = Arrangement.spacedBy(8.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            SecondaryButton("Back a page", onPrevPage) {
                Icon(Icons.Filled.ChevronLeft, contentDescription = null, modifier = Modifier.size(16.dp))
            }
            if (hasPrev) SecondaryButton("Previous chapter", onPrevChapter)
            if (hasNext) {
                Button(onClick = onNext, shape = MaterialTheme.shapes.small) {
                    Text("Next chapter", fontSize = 14.sp)
                    Icon(Icons.Filled.ChevronRight, contentDescription = null, modifier = Modifier.size(16.dp))
                }
            } else {
                SecondaryButton("Back to series", onExit)
            }
        }
    }
}

/**
 * Locked/paid chapter view. Purchase happens on the source site (extensions
 * expose no in-app buy), so the button opens it in the browser. Living in the
 * reader means unlocking never requires exiting and re-entering.
 */
@Composable
fun LockedChapterScreen(
    chapterLabel: String,
    url: String?,
    hasPrev: Boolean,
    hasNext: Boolean,
    checking: Boolean,
    onCheckNow: () -> Unit,
    onPrev: () -> Unit,
    onNext: () -> Unit,
    onExit: () -> Unit,
) {
    val context = LocalContext.current
    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(
            Icons.Filled.Lock,
            contentDescription = null,
            tint = ReaderPalette.Violet400,
            modifier = Modifier.size(40.dp),
        )
        Spacer(Modifier.height(24.dp))
        Eyebrow("Locked chapter")
        Text(
            chapterLabel,
            fontSize = 16.sp,
            fontWeight = FontWeight.Medium,
            color = ReaderPalette.Text85,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 4.dp),
        )
        Text(
            "This is a paid chapter. Buy it on the source site, then come back here — " +
                "it checks automatically and opens the chapter once it's unlocked.",
            fontSize = 14.sp,
            lineHeight = 19.sp,
            color = ReaderPalette.Text50,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 8.dp),
        )
        Spacer(Modifier.height(24.dp))
        if (url != null) {
            Button(
                onClick = {
                    runCatching {
                        context.startActivity(
                            Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                        )
                    }
                },
                shape = MaterialTheme.shapes.small,
                colors = ButtonDefaults.buttonColors(
                    containerColor = Color(0xFF8B5CF6), // violet-500
                    contentColor = Color.White,
                ),
            ) {
                Icon(Icons.Filled.OpenInNew, contentDescription = null, modifier = Modifier.size(16.dp))
                Spacer(Modifier.width(8.dp))
                Text("Buy / unlock on source", fontSize = 14.sp)
            }
        } else {
            Text(
                "No purchase link is available from the source.",
                fontSize = 14.sp,
                color = ReaderPalette.Text40,
            )
        }
        Spacer(Modifier.height(12.dp))
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .clip(RoundedCornerShape(8.dp))
                .border(1.dp, Color(0x33FFFFFF), RoundedCornerShape(8.dp))
                .background(Color(0x0FFFFFFF))
                .clickable(enabled = !checking, onClick = onCheckNow)
                .padding(horizontal = 16.dp, vertical = 10.dp),
        ) {
            if (checking) {
                CircularProgressIndicator(
                    color = ReaderPalette.Text70,
                    strokeWidth = 2.dp,
                    modifier = Modifier.size(14.dp),
                )
                Spacer(Modifier.width(8.dp))
                Text("Checking…", fontSize = 14.sp, color = ReaderPalette.Text80)
            } else {
                Text("I bought it — check again", fontSize = 14.sp, color = ReaderPalette.Text80)
            }
        }
        Spacer(Modifier.height(20.dp))
        Column(
            verticalArrangement = Arrangement.spacedBy(8.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            if (hasPrev) {
                SecondaryButton("Previous", onPrev) {
                    Icon(Icons.Filled.ChevronLeft, contentDescription = null, modifier = Modifier.size(16.dp))
                }
            }
            if (hasNext) {
                SecondaryButton("Next", onNext) {
                    Icon(Icons.Filled.ChevronRight, contentDescription = null, modifier = Modifier.size(16.dp))
                }
            }
            Text(
                "Back to series",
                fontSize = 14.sp,
                color = ReaderPalette.Text70,
                modifier = Modifier.clickable(onClick = onExit).padding(8.dp),
            )
        }
    }
}

/** The "Finished / Up next" divider between two chapters in the continuous strip. */
@Composable
fun ChapterDivider(finishedLabel: String, nextLabel: String, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Eyebrow("Finished")
        Text(
            finishedLabel,
            fontSize = 14.sp,
            fontWeight = FontWeight.Medium,
            color = ReaderPalette.Text70,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 2.dp),
        )
        Box(
            modifier = Modifier
                .padding(vertical = 12.dp)
                .width(64.dp)
                .height(1.dp)
                .background(Color(0x26FFFFFF)),
        )
        Eyebrow("Up next")
        Text(
            nextLabel,
            fontSize = 14.sp,
            fontWeight = FontWeight.Medium,
            color = RenzoColors.Primary,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 2.dp),
        )
    }
}
