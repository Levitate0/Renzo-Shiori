package app.renzoshiori.client.ui.auth

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.R
import app.renzoshiori.client.ui.tv.LocalIsTv
import app.renzoshiori.client.ui.tv.focusRing

// The original connect panel's palette (res/values/colors.xml) — kept verbatim
// so the native rewrite is pixel-faithful to the screen users already know.
private val Bg = Color(0xFF0A0A0A)
private val Fg = Color(0xFFFAFAFA)
private val Muted = Color(0xFFA1A1AA)
private val Subtle = Color(0xFF71717A)
private val Border = Color(0xFF3F3F46)
private val ErrorRed = Color(0xFFEF4444)

/**
 * Faithful Compose port of the original connect panel (res/layout/
 * activity_main.xml serverPanel): banner art, "Connect to your server",
 * address field, example help line, white full-width Connect button.
 */
@Composable
fun ConnectScreen(
    loading: Boolean,
    error: String?,
    onConnect: (String) -> Unit,
) {
    var address by remember { mutableStateOf("") }
    val isTv = LocalIsTv.current
    var fieldFocused by remember { mutableStateOf(false) }
    val fieldRequester = remember { FocusRequester() }

    fun submit() {
        if (!loading && address.isNotBlank()) onConnect(address)
    }

    // On a remote there is no tap to place the cursor: land in the one field
    // this screen has, so the leanback IME is one Centre press away.
    LaunchedEffect(isTv) {
        if (isTv) runCatching { fieldRequester.requestFocus() }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(32.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Image(
            painter = painterResource(R.drawable.renzo_banner),
            contentDescription = "Renzo Shiori",
            modifier = Modifier.width(240.dp).padding(bottom = 20.dp),
        )
        Text(
            "Connect to your server",
            color = Muted,
            fontSize = 14.sp,
            modifier = Modifier.padding(bottom = 24.dp),
        )
        OutlinedTextField(
            value = address,
            onValueChange = { address = it },
            placeholder = { Text("https://renzo-shiori.example.com", color = Subtle) },
            singleLine = true,
            // Declared IME action so the leanback keyboard can submit the
            // address without hunting for a button afterwards.
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Go),
            keyboardActions = KeyboardActions(onGo = { submit() }),
            colors = OutlinedTextFieldDefaults.colors(
                focusedTextColor = Fg,
                unfocusedTextColor = Fg,
                focusedBorderColor = Border,
                unfocusedBorderColor = Border,
                cursorColor = Fg,
            ),
            modifier = Modifier
                .fillMaxWidth()
                .focusRequester(fieldRequester)
                .onFocusChanged { fieldFocused = it.hasFocus }
                .focusRing(isTv && fieldFocused, 8.dp),
        )
        Text(
            "e.g. https://renzo-shiori.example.com or http://192.168.1.10:8080",
            color = Subtle,
            fontSize = 11.sp,
            modifier = Modifier.fillMaxWidth().padding(top = 6.dp, bottom = 20.dp),
        )
        var buttonFocused by remember { mutableStateOf(false) }
        Button(
            shape = MaterialTheme.shapes.small, onClick = { submit() },
            enabled = !loading && address.isNotBlank(),
            colors = ButtonDefaults.buttonColors(
                containerColor = Fg,
                contentColor = Bg,
                disabledContainerColor = Fg.copy(alpha = 0.4f),
                disabledContentColor = Bg.copy(alpha = 0.6f),
            ),
            modifier = Modifier
                .fillMaxWidth()
                .onFocusChanged { buttonFocused = it.isFocused }
                .focusRing(isTv && buttonFocused, 8.dp),
        ) {
            Text(if (loading) "Connecting…" else "Connect")
        }
        if (error != null) {
            Text(
                error,
                color = ErrorRed,
                fontSize = 13.sp,
                modifier = Modifier.fillMaxWidth().padding(top = 16.dp),
            )
        }
        if (loading) {
            CircularProgressIndicator(
                color = Fg,
                modifier = Modifier.padding(top = 16.dp).size(28.dp),
                strokeWidth = 3.dp,
            )
        }
    }
}
