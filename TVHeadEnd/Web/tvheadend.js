const TVHclientConfigurationPageVar = {
    pluginUniqueId: '980dc09b-7127-4a6e-b101-b6ae374ea0cc'
};

function reportError(action, err) {
    Dashboard.hideLoadingMsg();
    const status = err && err.status ? ' (HTTP ' + err.status + ')' : '';
    console.error('TVHeadend plugin: failed to ' + action + ' configuration', err);
    Dashboard.alert('Failed to ' + action + ' TVHeadend plugin configuration' + status);
}

export default function (view, params) {
    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        const page = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(function(config) {
            page.querySelector('#txtTVH_ServerName').value = config.TVH_ServerName || '';
            page.querySelector('#txtHTTP_Port').value = config.HTTP_Port || '9981';
            page.querySelector('#txtHTSP_Port').value = config.HTSP_Port || '9982';
            page.querySelector('#txtWebRoot').value = config.WebRoot || '/';
            page.querySelector('#txtUserName').value = config.Username || '';
            page.querySelector('#txtPassword').value = config.Password || '';
            page.querySelector('#txtPriority').value = config.Priority || '5';
            page.querySelector('#txtProfile').value = config.Profile || '';
            page.querySelector('#txtPrePadding').value = config.Pre_Padding || '0';
            page.querySelector('#txtPostPadding').value = config.Post_Padding || '0';
            page.querySelector('#selChannelType').value = config.ChannelType || 'Ignore';
            page.querySelector('#chkIncludeUnnumberedChannels').checked = config.IncludeUnnumberedChannels !== false;
            page.querySelector('#chkHideRecordingsChannel').checked = config.HideRecordingsChannel || false;
            page.querySelector('#chkEnableSubsMaudios').checked = config.EnableSubsMaudios || false;
            page.querySelector('#chkForceDeinterlace').checked = config.ForceDeinterlace || false;
            Dashboard.hideLoadingMsg();
        }).catch(function (err) {
            reportError('load', err);
        });
    });
    view.querySelector('.TVHclientConfigurationForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        const form = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(function(config) {
            config.TVH_ServerName = form.querySelector('#txtTVH_ServerName').value;
            config.HTTP_Port = form.querySelector('#txtHTTP_Port').value;
            config.HTSP_Port = form.querySelector('#txtHTSP_Port').value;
            config.WebRoot = form.querySelector('#txtWebRoot').value;
            config.Username = form.querySelector('#txtUserName').value;
            config.Password = form.querySelector('#txtPassword').value;
            config.Priority = form.querySelector('#txtPriority').value;
            config.Profile = form.querySelector('#txtProfile').value;
            config.Pre_Padding = form.querySelector('#txtPrePadding').value;
            config.Post_Padding = form.querySelector('#txtPostPadding').value;
            config.ChannelType = form.querySelector('#selChannelType').value;
            config.IncludeUnnumberedChannels = form.querySelector('#chkIncludeUnnumberedChannels').checked;
            config.HideRecordingsChannel = form.querySelector('#chkHideRecordingsChannel').checked;
            config.EnableSubsMaudios = form.querySelector('#chkEnableSubsMaudios').checked;
            config.ForceDeinterlace = form.querySelector('#chkForceDeinterlace').checked;
            return ApiClient.updatePluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId, config).then(Dashboard.processPluginConfigurationUpdateResult);
        }).catch(function (err) {
            reportError('save', err);
        });
        return false;
    });
}
