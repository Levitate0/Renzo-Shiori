package app.renzoshiori.client.ui.auth

import android.graphics.BitmapFactory
import android.util.Base64
import androidx.compose.foundation.Image
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Person
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.R
import app.renzoshiori.client.data.model.UserDto
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.LocalIsTv
import app.renzoshiori.client.ui.tv.focusRing
import app.renzoshiori.client.ui.tv.rememberFocusState
import app.renzoshiori.client.ui.tv.tvClickable
import app.renzoshiori.client.ui.tv.tvContentColor

/**
 * RenzoFrontend/src/app/user-select/page.tsx — the profile picker the web app
 * shows when authentication is disabled: logo, "Select your user to continue",
 * and one outline row per user (avatar or person icon + username), or the
 * "No users found" line when the server returned an empty list.
 */
@Composable
fun UserSelectScreen(
    users: List<UserDto>,
    loading: Boolean,
    error: String?,
    onSelectUser: (String) -> Unit,
) {
    AuthPageScaffold {
        AuthCard {
            AuthCardHeader(spacing = 12.dp) {
                Box(modifier = Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                    Image(
                        painter = painterResource(R.drawable.splash_icon),
                        contentDescription = "Renzo Shiori",
                        modifier = Modifier.size(80.dp),
                    )
                }
                AuthCardDescription("Select your user to continue")
            }
            AuthCardContent {
                if (error != null) {
                    AuthErrorBox(error)
                    Spacer(Modifier.height(8.dp))
                }
                if (loading) {
                    AuthNoticeBox("Signing in…")
                    Spacer(Modifier.height(8.dp))
                }
                if (users.isEmpty()) {
                    Text(
                        "No users found. Please contact your administrator.",
                        color = RenzoColors.MutedForeground,
                        fontSize = 14.sp,
                        textAlign = TextAlign.Center,
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.fillMaxWidth(),
                    )
                } else {
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        users.forEachIndexed { index, user ->
                            UserSelectRow(
                                user = user,
                                // The one sign-in path that needs no typing at
                                // all, so the cursor starts on it.
                                autoFocus = index == 0,
                                onClick = { onSelectUser(user.username) },
                            )
                        }
                    }
                }
            }
        }
    }
}

/** `variant="outline" className="w-full justify-start gap-3 h-14"` */
@Composable
private fun UserSelectRow(user: UserDto, autoFocus: Boolean = false, onClick: () -> Unit) {
    val shape = RoundedCornerShape(6.dp)
    val isTv = LocalIsTv.current
    val focus = rememberFocusState()
    val requester = remember { FocusRequester() }
    LaunchedEffect(isTv, autoFocus) {
        if (isTv && autoFocus) runCatching { requester.requestFocus() }
    }
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier
            .fillMaxWidth()
            .height(56.dp)
            .clip(shape)
            .background(if (isTv && focus.focused) RenzoColors.Card else RenzoColors.Background)
            .border(1.dp, RenzoColors.Border, shape)
            .focusRing(isTv && focus.focused, 6.dp)
            .then(
                if (isTv) {
                    Modifier.focusRequester(requester).tvClickable(onFocused = focus::set) { onClick() }
                } else {
                    Modifier.clickable { onClick() }
                },
            )
            .padding(horizontal = 16.dp),
    ) {
        UserAvatar(user)
        Text(
            user.username,
            color = if (isTv) tvContentColor(false, focus.focused) else RenzoColors.Foreground,
            fontSize = 14.sp,
            fontWeight = FontWeight.Medium,
            style = MaterialTheme.typography.bodyMedium,
        )
    }
}

/** `w-8 h-8 rounded-full bg-muted` holding the base64 avatar, or a UserIcon. */
@Composable
private fun UserAvatar(user: UserDto) {
    val bitmap = remember(user.avatarBase64) {
        user.avatarBase64?.takeIf { it.isNotBlank() }?.let { encoded ->
            runCatching {
                val bytes = Base64.decode(encoded, Base64.DEFAULT)
                BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
            }.getOrNull()
        }
    }
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .size(32.dp)
            .clip(CircleShape)
            .background(RenzoColors.Muted),
    ) {
        if (bitmap != null) {
            Image(
                bitmap = bitmap,
                contentDescription = user.username,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        } else {
            Icon(
                Icons.Filled.Person,
                contentDescription = null,
                tint = RenzoColors.Foreground,
                modifier = Modifier.size(16.dp),
            )
        }
    }
}
